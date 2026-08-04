# Rebuilds icons.json from every image file in this folder.
#
# Drop a logo in here named after the key you want (twitch.png, obs.webp, discord.png…)
# then run:  powershell -ExecutionPolicy Bypass -File make-icons.ps1
#
# Each image is base64-encoded into icons.json as a data URI, so the app ships one
# file instead of a pile of loose images. The originals stay here as the source.

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

$mime = @{
  '.png'  = 'image/png'
  '.webp' = 'image/webp'
  '.jpg'  = 'image/jpeg'
  '.jpeg' = 'image/jpeg'
  '.gif'  = 'image/gif'
  '.svg'  = 'image/svg+xml'
}

$map = [ordered]@{}
Get-ChildItem $dir -File | Where-Object { $mime.ContainsKey($_.Extension.ToLower()) } | Sort-Object Name | ForEach-Object {
  $key = $_.BaseName.ToLower()
  if ($key -eq 'icon') { return }        # the app icon is handled separately
  $type = $mime[$_.Extension.ToLower()]
  $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
  $map[$key] = "data:$type;base64,$b64"
  "{0,-12} <- {1} ({2:N0} KB encoded)" -f $key, $_.Name, ($b64.Length / 1KB)
}

($map | ConvertTo-Json -Compress) | Set-Content (Join-Path $dir 'icons.json') -Encoding UTF8
"`nicons.json: {0} icons, {1:N1} KB" -f $map.Count, ((Get-Item (Join-Path $dir 'icons.json')).Length / 1KB)
