$files = @('./SlashCommands.cs')
$success = $true;
foreach ($file in $files) {
    $content = Get-Content $file
    $regexMatches = [regex]::Matches($content, '(\[(SlashCommand|Option)\(\s*\"(.*?)"\s*?,\s*\"(.*?)\"\s*(,\s*\"(.*?)\"\s*)*\)\])', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach($match in $regexMatches){
        Write-Output $match.Groups[4].Value.Length
        Write-Output $match.Value
        if($match.Groups[4].Value[0] -cmatch "[a-z]"){
            $success = $false
            $error_string = "`""+$match.Groups[4].Value+"`" Starts with a lower case letter: "+$match.Groups[4].Value[0]
            Write-Error $error_string
        }
    }
}
if ($success) {
    Write-Output "All Good"
} else {
    Write-Error "Failure"
}