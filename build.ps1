$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$out = Join-Path $PSScriptRoot 'Jeapfrog.exe'
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/win32manifest:$source\jeapfrog.manifest", "/out:$out",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Net.Http.dll',
    "/resource:$source\bridge.py,bridge.py", "/resource:$source\catalog.txt,catalog.txt",
    "$source\Engine.cs", "$source\Jeapfrog.cs"
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Jeapfrog build failed.' }
Write-Output $out
