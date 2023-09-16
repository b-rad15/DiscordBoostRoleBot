$files = @('./SlashCommands.cs')
$success = $true;
foreach ($file in $files) {
    $content = Get-Content $file
    $regexMatches = [regex]::Matches($content, '(\[(SlashCommand|Option)\(\s*\"(.*?)\"\s*?\,\s*?\"(.*?)\"\s*?(\,\s*?\"(.*?)\"\s*?)*?.*?\)\])', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach($match in $regexMatches){
        Write-Output $match.Groups[4].Value.Length
        Write-Output $match.Value
        if($match.Groups[4].Value.Length -gt 100){
            $success = $false
            $error_string = "`""+$match.Groups[4].Value+"`" Description is too long, "+$match.Groups[4].Value.Length+" characters"
            Write-Error $error_string
        }
    }
}
if ($success) {
    Write-Output "All Good"
} else {
    Write-Error "Failure"
}