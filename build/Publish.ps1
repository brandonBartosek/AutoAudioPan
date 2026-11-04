[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath("Desktop")) "AutoAudioPan")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\AutoAudioPan\AutoAudioPan.csproj"
$marker = Join-Path $OutputPath ".autoaudiopan-owned"
$allowed = @(".autoaudiopan-owned", "AutoAudioPan.exe", "AutoAudioPan.pdb", "settings.json")

if (Test-Path $OutputPath) {
    $unknown = Get-ChildItem -Force $OutputPath | Where-Object { $allowed -notcontains $_.Name }
    if ($unknown) {
        $names = ($unknown | ForEach-Object Name) -join ", "
        throw "Refusing to publish into '$OutputPath' because it contains unrelated files: $names"
    }
} else {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("AutoAudioPan-publish-" + [Guid]::NewGuid().ToString("N"))
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $staging
    Get-ChildItem -Force $staging | Copy-Item -Destination $OutputPath -Force
    Set-Content -Path $marker -Value "Files in this directory are owned by AutoAudioPan.Publish.ps1."
    Write-Output "Published AutoAudioPan to $OutputPath"
}
finally {
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
}
