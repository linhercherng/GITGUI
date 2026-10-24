Option Explicit
Dim fs, shell, root, command, argument, child, diagnostic
Set fs = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fs.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root
command = Quote(fs.BuildPath(fs.GetSpecialFolder(0), "System32\rundll32.exe")) & " " & Quote(fs.BuildPath(root, "GitGui.Host.dll")) & ",RunW"
diagnostic = False
For Each argument In WScript.Arguments
    If argument = "--performance-report" Then diagnostic = True
    command = command & " " & Quote(argument)
Next
' Exec invokes the GUI host directly, without cmd.exe, and keeps this launcher alive.
' Its OS creation time is included by the native desktop cold-start probe.
Set child = shell.Exec(command)
Do While child.Status = 0
    WScript.Sleep 20
Loop
If child.ExitCode <> 0 And Not diagnostic Then
    MsgBox "GITGUI could not start (exit " & child.ExitCode & "). Keep the extracted folder complete and see START-HERE.txt.", 16, "GITGUI"
End If
WScript.Quit child.ExitCode

Function Quote(value)
    Dim result, slashes, i, character
    result = Chr(34)
    slashes = 0
    For i = 1 To Len(value)
        character = Mid(value, i, 1)
        If character = "\" Then
            slashes = slashes + 1
        ElseIf character = Chr(34) Then
            result = result & String(slashes * 2 + 1, "\") & Chr(34)
            slashes = 0
        Else
            result = result & String(slashes, "\") & character
            slashes = 0
        End If
    Next
    Quote = result & String(slashes * 2, "\") & Chr(34)
End Function
