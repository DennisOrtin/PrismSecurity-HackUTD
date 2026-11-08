# Regenerate client bindings after EVERY module change. Both people run this after pulling.
$out = "$PSScriptRoot\..\src\IncidentCommand.Client\module_bindings"
Get-ChildItem $out -Filter *.cs -Recurse | Remove-Item
spacetime generate --lang csharp --out-dir $out --module-path "$PSScriptRoot\..\server"
