$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root 'AiMcpServer/bin/Debug/net10.0/AiMcpServer.dll'
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('AiMcpServerHttpTests-' + [Guid]::NewGuid().ToString('N'))
$database = Join-Path $directory 'memory.db'
$endpoint = 'http://localhost:5050/mcp'
$script:server = $null

function Invoke-Mcp($method, $parameters) {
	$body = @{ jsonrpc = '2.0'; id = 1; method = $method; params = $parameters } | ConvertTo-Json -Depth 20 -Compress
	$response = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post -ContentType 'application/json' -Headers @{ Accept = 'application/json, text/event-stream' } -Body $body
	$text = [string]$response.Content
	if ($text.TrimStart().StartsWith('{')) {
		$message = $text | ConvertFrom-Json
	}
	else {
		$data = @($text -split "`n" | Where-Object { $_.StartsWith('data: ') })
		if ($data.Count -ne 1) { throw "Unexpected MCP response: $text" }
		$message = $data[0].Substring(6) | ConvertFrom-Json
	}
	if ($message.error -or $message.result.isError) { throw "MCP error: $text" }
	return $message.result
}

function Start-TestServer {
	$script:server = Start-Process -FilePath 'dotnet' -ArgumentList @(('"' + $dll + '"'), ('"--Memory:DatabasePath=' + $database + '"')) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $directory 'stdout.log') -RedirectStandardError (Join-Path $directory 'stderr.log')
	for ($attempt = 0; $attempt -lt 60; $attempt++) {
		if ($script:server.HasExited) { throw ('Server exited: ' + (Get-Content (Join-Path $directory 'stderr.log') -Raw)) }
		try {
			$initialized = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'memory-smoke-test'; version = '1.0' } }
			if ($initialized.serverInfo) { return }
		}
		catch {
			if ($attempt -eq 59) { throw }
			Start-Sleep -Milliseconds 250
		}
	}
	throw 'Server did not become ready.'
}

function Stop-TestServer {
	if ($script:server -and !$script:server.HasExited) {
		Stop-Process -Id $script:server.Id -Force
		$script:server.WaitForExit()
	}
	if ($script:server) { $script:server.Dispose(); $script:server = $null }
}

function Assert-Forbidden($headers) {
	try {
		Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Headers $headers | Out-Null
	}
	catch {
		if ([int]$_.Exception.Response.StatusCode -eq 403) { return }
		throw
	}
	throw 'Expected HTTP 403.'
}

try {
	if (!(Test-Path $dll)) { throw 'Build the solution in Debug before running this test.' }
	$probe = New-Object System.Net.Sockets.TcpClient
	try {
		$probe.Connect('localhost', 5050)
		throw 'Port 5050 is already in use. Stop the development server before running this test.'
	}
	catch [System.Net.Sockets.SocketException] {
	}
	finally { $probe.Dispose() }
	New-Item -ItemType Directory -Path $directory | Out-Null
	Start-TestServer
	$tools = Invoke-Mcp 'tools/list' @{}
	foreach ($name in @('save_memory', 'get_task_context', 'search_memory', 'get_random_number')) {
		if ($tools.tools.name -notcontains $name) { throw "Missing tool: $name" }
	}
	$marker = [Guid]::NewGuid().ToString('N')
	$saved = Invoke-Mcp 'tools/call' @{ name = 'save_memory'; arguments = @{ projectId = 'smoke-project'; taskId = 'task-1'; agentId = 'agent-1'; content = $marker } }
	if (($saved | ConvertTo-Json -Depth 20) -notmatch $marker) { throw 'Save did not return the written memory.' }
	$context = Invoke-Mcp 'tools/call' @{ name = 'get_task_context'; arguments = @{ projectId = 'smoke-project'; taskId = 'task-1' } }
	if (($context | ConvertTo-Json -Depth 20) -notmatch $marker) { throw 'Memory was not retrieved.' }
	$search = Invoke-Mcp 'tools/call' @{ name = 'search_memory'; arguments = @{ projectId = 'smoke-project'; query = $marker } }
	if (($search | ConvertTo-Json -Depth 20) -notmatch $marker) { throw 'Memory search failed.' }
	$isolated = Invoke-Mcp 'tools/call' @{ name = 'get_task_context'; arguments = @{ projectId = 'other-project'; taskId = 'task-1' } }
	if (($isolated | ConvertTo-Json -Depth 20) -match $marker) { throw 'Memory leaked between projects.' }
	Assert-Forbidden @{ Origin = 'https://untrusted.example' }
	Assert-Forbidden @{ Host = 'untrusted.example:5050' }
	Stop-TestServer
	Start-TestServer
	$restored = Invoke-Mcp 'tools/call' @{ name = 'get_task_context'; arguments = @{ projectId = 'smoke-project'; taskId = 'task-1' } }
	if (($restored | ConvertTo-Json -Depth 20) -notmatch $marker) { throw 'Memory did not survive server restart.' }
	Write-Output 'HTTP smoke tests passed: initialization, tool discovery, save/read/search, isolation, origin/host rejection, and restart persistence.'
}
finally {
	Stop-TestServer
	if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
}
