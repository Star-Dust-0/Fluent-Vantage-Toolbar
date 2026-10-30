# ============================================================================
# windows-vm-test guest script - runs INSIDE the Windows 11 VM at first boot.
# Template: set these three variables per repo, nothing else changes.
# ============================================================================
$Repo       = 'Star-Dust-0/Fluent-Vantage-Toolbar'   # GitHub repo with the release
$AssetMatch = 'portable'                # regex on the release asset name
$ExeMatch   = 'FluentLegionToolbar'            # regex on the exe file/process name
# ============================================================================
$ErrorActionPreference='Continue'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
function Shot($n){ $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bm=New-Object System.Drawing.Bitmap $b.Width,$b.Height; $g=[System.Drawing.Graphics]::FromImage($bm); $g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); $bm.Save("\\host.lan\Data\$n.png"); "shot $n $($b.Width)x$($b.Height)" }
Shot 'desktop'
$rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
"release: $($rel.tag_name)"; $rel.assets | % { "asset: $($_.name) $($_.size)" }
$a = $rel.assets | ? { $_.name -match $AssetMatch } | select -First 1
if (-not $a) { "ERROR: no release asset matches '$AssetMatch'"; exit 1 }
Invoke-WebRequest $a.browser_download_url -OutFile C:\app.zip
Expand-Archive C:\app.zip C:\app -Force
$exe = Get-ChildItem C:\app -Recurse -Filter *.exe | ? { $_.Name -match $ExeMatch } | select -First 1
if (-not $exe) { "ERROR: no exe matches '$ExeMatch'"; exit 1 }
"exe: $($exe.FullName)"
Start-Process $exe.FullName
Start-Sleep 25
Get-Process | ? { $_.Name -match $ExeMatch } | % { "proc: $($_.Name)" }
Shot 'app'
