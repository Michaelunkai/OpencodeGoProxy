param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot 'OpencodeGoProxy.cs'
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $projectRoot 'OpencodeGoProxy.exe' } else { [IO.Path]::GetFullPath($OutputPath) }
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw 'The in-box .NET Framework C# compiler was not found. No third-party runtime was installed.'
}
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'Proxy source file is missing.' }
& $compilerPath /nologo /optimize+ /target:exe "/out:$outputPath" /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll  $sourcePath
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
Write-Output "BUILD_OK $outputPath"
