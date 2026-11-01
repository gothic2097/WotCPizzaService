param([string]$Executable = "$PSScriptRoot\..\x64\Debug\WotCPizzaServer.exe")
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('WotCPizzaNetwork-' + [guid]::NewGuid().ToString())
$null = New-Item -ItemType Directory -Path $directory
$process = $null
$checks = 0
function Assert-True($Condition, [string]$Message) {
	if (-not $Condition) { throw $Message }
	$script:checks++
}
function Send-Text([string]$Text, [switch]$Fragmented) {
	$client = [Net.Sockets.TcpClient]::new()
	try {
		$client.Connect('127.0.0.1', $script:port)
		$stream = $client.GetStream()
		$stream.ReadTimeout = 7000
		$stream.WriteTimeout = 7000
		$bytes = [Text.Encoding]::UTF8.GetBytes($Text + "`n")
		if ($Fragmented) {
			$half = [int][Math]::Floor($bytes.Length / 2)
			$stream.Write($bytes, 0, $half)
			Start-Sleep -Milliseconds 20
			$stream.Write($bytes, $half, $bytes.Length - $half)
		} else { $stream.Write($bytes, 0, $bytes.Length) }
		$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
		$line = $reader.ReadLine()
		if ($null -eq $line) { throw 'Server closed without a response.' }
		return ($line | ConvertFrom-Json)
	} finally { $client.Dispose() }
}
function Invoke-Message($Message, [switch]$Fragmented) {
	return Send-Text ($Message | ConvertTo-Json -Depth 30 -Compress) -Fragmented:$Fragmented
}
function Start-TestServer {
	$script:process = Start-Process -FilePath $Executable -ArgumentList @('--data', ('"' + $directory + '"'), '--port', $script:port) -PassThru -RedirectStandardOutput (Join-Path $directory 'stdout.log') -RedirectStandardError (Join-Path $directory 'stderr.log')
	for ($i = 0; $i -lt 100; $i++) {
		if ($script:process.HasExited) { throw ('Server exited: ' + (Get-Content (Join-Path $directory 'stderr.log') -Raw)) }
		try { $response = Invoke-Message @{ FunctionId = 0 }; if ($response.resultCode -eq 0) { return } } catch { Start-Sleep -Milliseconds 50 }
	}
	throw 'Server startup timed out.'
}
function Stop-TestServer {
	if ($script:process -and -not $script:process.HasExited) {
		Stop-Process -Id $script:process.Id -Force
		$script:process.WaitForExit()
	}
	if ($script:process) { $script:process.Dispose(); $script:process = $null }
}
try {
	$menu = Get-Content (Join-Path (Split-Path $Executable) 'menu.json') -Raw | ConvertFrom-Json
	$menu.settings.startDelaySeconds = 60
	$menu.menuVersion = 'network-test'
	[IO.File]::WriteAllText((Join-Path $directory 'menu.json'), ($menu | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
	$reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$reservation.Start(); $script:port = $reservation.LocalEndpoint.Port; $reservation.Stop()
	Start-TestServer
	Assert-True ((Send-Text '{').resultCode -eq 3) 'Malformed JSON was accepted.'
	Assert-True ((Invoke-Message @{ FunctionId = 99 }).resultCode -eq 2) 'Unknown function was accepted.'
	Assert-True ((Invoke-Message @{ FunctionId = 3; Input = @{} }).resultCode -eq 3) 'Missing fields were accepted.'
	$request = @{ orderId = [guid]::NewGuid().ToString(); menuVersion = 'network-test'; pizzas = @(@{ pizzaCode = 0; count = 1; toppings = @() }); promoCodes = @(); tipCents = 0; tipBasisPoints = 0 }
	$request.totals = @{ totalAfterTaxAndTipCents = 1 }
	$request.status = 3
	$place = @{ FunctionId = 3; Input = @{ customerId = 42; customerOrder = $request } }
	$first = Invoke-Message $place -Fragmented
	Assert-True ($first.resultCode -eq 0) 'Fragmented placement failed.'
	Assert-True ($first.customerOrder.totals.totalAfterTaxAndTipCents -eq 532) 'Client-supplied total was trusted.'
	Assert-True ($first.customerOrder.status -eq 0) 'Client-supplied status was trusted.'
	Assert-True ((Invoke-Message $place).resultCode -eq 4) 'Submission retry created a duplicate.'
	$query = @{ FunctionId = 1; Input = @{ customerId = 42; orderId = $request.orderId } }
	Assert-True ((Invoke-Message $query).customerOrder.status -eq 0) 'GetOrder failed.'
	$request.pizzas[0].count = 2
	Assert-True ((Invoke-Message $place).error -eq 'idempotency_conflict') 'Changed submission was accepted.'
	$update = @{ FunctionId = 5; Input = @{ customerId = 42; expectedRevision = 1; customerOrder = $request } }
	Assert-True ((Invoke-Message $update).customerOrder.revision -eq 2) 'Update failed.'
	Assert-True ((Invoke-Message $update).customerOrder.revision -eq 2) 'Update retry applied twice.'
	Stop-TestServer
	Start-TestServer
	Assert-True ((Invoke-Message $query).customerOrder.revision -eq 2) 'Update was lost across process restart.'
	$request.pizzas[0].count = 1
	Assert-True ((Invoke-Message $place).resultCode -eq 4) 'Original submission deduplication was lost on restart.'
	$query.FunctionId = 6
	Assert-True ((Invoke-Message $query).customerOrder.status -eq 4) 'Cancellation failed.'
	Assert-True ((Invoke-Message $query).resultCode -eq 0) 'Cancellation retry failed.'
	$query.FunctionId = 8
	Assert-True ((Invoke-Message $query).resultCode -eq -1) 'Cancelled order was delivered.'
	$query.FunctionId = 4
	Assert-True ((Invoke-Message $query).status -eq 4) 'GetOrderStatus failed.'
	$request.orderId = [guid]::NewGuid().ToString()
	$lostResponse = [Net.Sockets.TcpClient]::new()
	try {
		$lostResponse.Connect('127.0.0.1', $script:port)
		$stream = $lostResponse.GetStream()
		$stream.WriteTimeout = 7000
		$bytes = [Text.Encoding]::UTF8.GetBytes(($place | ConvertTo-Json -Depth 30 -Compress) + "`n")
		$stream.Write($bytes, 0, $bytes.Length)
		$lookup = @{ FunctionId = 1; Input = @{ customerId = 42; orderId = $request.orderId } }
		for ($i = 0; $i -lt 50; $i++) {
			$observed = Invoke-Message $lookup
			if ($observed.resultCode -eq 0) { break }
			Start-Sleep -Milliseconds 20
		}
		Assert-True ($observed.resultCode -eq 0) 'Placement was not saved before its unread response was discarded.'
	} finally { $lostResponse.Dispose() }
	Assert-True ((Invoke-Message $place).resultCode -eq 4) 'Retry after losing the placement response created another order.'
	$all = Invoke-Message @{ FunctionId = 2; Input = @{ customerId = 42 } }
	Assert-True ($all.customerOrders.Count -eq 2) 'Duplicate order found.'
	Write-Host "$checks network checks passed."
} finally {
	Stop-TestServer
	Remove-Item -Recurse -Force $directory
}
