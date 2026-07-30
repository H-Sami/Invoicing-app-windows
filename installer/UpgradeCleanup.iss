[Code]
const
  FileAttributeDirectory = $10;
  FileAttributeReparsePoint = $400;
  PayloadManifestName = '.mhc-payload-manifest.txt';
  ShellChangeAssociationChanged = $08000000;
  ShellChangeIdList = $0000;

procedure SHChangeNotify(EventId: LongWord; Flags: LongWord;
  Item1: LongWord; Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';

function IsPreservedUninstallerFile(const FileName: String): Boolean;
var
  LowerName: String;
  Extension: String;
  Stem: String;
begin
  LowerName := Lowercase(ExtractFileName(FileName));
  Extension := Lowercase(ExtractFileExt(LowerName));
  Stem := ChangeFileExt(LowerName, '');
  Result := (Length(Stem) = 8) and (Copy(Stem, 1, 5) = 'unins') and
    (StrToIntDef(Copy(Stem, 6, 3), -1) >= 0) and
    ((Extension = '.exe') or (Extension = '.dat') or (Extension = '.msg'));
end;

function NormalizeRelativePath(const RelativePath: String): String;
begin
  Result := Lowercase(Trim(RelativePath));
  StringChangeEx(Result, '/', '\', True);
end;

function IsCurrentPayloadFile(const RelativePath: String;
  const Manifest: TArrayOfString): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to GetArrayLength(Manifest) - 1 do
  begin
    if CompareText(Manifest[Index], NormalizeRelativePath(RelativePath)) = 0 then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function LoadAndValidatePayloadManifest(const ManifestPath: String;
  var Manifest: TArrayOfString): Boolean;
var
  Index: Integer;
  Entry: String;
begin
  Result := False;
  if not LoadStringsFromFile(ManifestPath, Manifest) then
  begin
    Log('Payload manifest is unavailable; obsolete cleanup is skipped: ' + ManifestPath);
    exit;
  end;

  for Index := 0 to GetArrayLength(Manifest) - 1 do
  begin
    Entry := NormalizeRelativePath(Manifest[Index]);
    if Entry = '' then
    begin
      Log('Payload manifest contains an empty path; obsolete cleanup is skipped.');
      exit;
    end
    else if (Entry[1] = '\') or (Pos(':', Entry) > 0) or
      (Entry = '..') or (Pos('..\', Entry) = 1) or
      (Pos('\..\', Entry) > 0) or
      (Copy(Entry, Length(Entry) - 2, 3) = '\..') then
    begin
      Log('Payload manifest contains an unsafe path; obsolete cleanup is skipped: ' + Entry);
      exit;
    end
    else
      Manifest[Index] := Entry;
  end;

  Result := IsCurrentPayloadFile(PayloadManifestName, Manifest);
  if not Result then
    Log('Payload manifest does not identify itself; obsolete cleanup is skipped.');
end;

function CleanObsoletePayload(const Directory: String;
  const RelativeDirectory: String; const IsAppRoot: Boolean;
  const Manifest: TArrayOfString): Boolean;
var
  FindRec: TFindRec;
  EntryPath: String;
  RelativePath: String;
  EntryIsDirectory: Boolean;
begin
  Result := True;
  if not DirExists(Directory) then
    exit;

  if FindFirst(AddBackslash(Directory) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          EntryPath := AddBackslash(Directory) + FindRec.Name;
          if RelativeDirectory = '' then
            RelativePath := FindRec.Name
          else
            RelativePath := AddBackslash(RelativeDirectory) + FindRec.Name;
          EntryIsDirectory := (FindRec.Attributes and FileAttributeDirectory) <> 0;

          { Inno owns these files and updates them after CurStepChanged(ssPostInstall). }
          if IsAppRoot and (not EntryIsDirectory) and
            IsPreservedUninstallerFile(FindRec.Name) then
          begin
            Log('Preserving installer-owned uninstall file: ' + EntryPath);
          end
          else if EntryIsDirectory then
          begin
            { Never follow a junction/symlink outside the app root. }
            if (FindRec.Attributes and FileAttributeReparsePoint) <> 0 then
            begin
              if not RemoveDir(EntryPath) then
              begin
                Log('Failed to remove obsolete app-root reparse point: ' + EntryPath);
                Result := False;
              end;
            end
            else
            begin
              if not CleanObsoletePayload(EntryPath, RelativePath, False, Manifest) then
                Result := False;
              { This succeeds only for a now-empty obsolete directory. Current
                directories remain because their manifest-listed files remain. }
              RemoveDir(EntryPath);
            end;
          end
          else if not IsCurrentPayloadFile(RelativePath, Manifest) then
          begin
            if DeleteFile(EntryPath) then
              Log('Removed obsolete installed payload after successful install: ' + EntryPath)
            else
            begin
              Log('Failed to remove obsolete installed payload: ' + EntryPath);
              Result := False;
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDirectory: String;
  ManifestPath: String;
  Manifest: TArrayOfString;
begin
  if CurStep <> ssPostInstall then
    exit;

  AppDirectory := ExpandConstant('{app}');
  ManifestPath := AddBackslash(AppDirectory) + PayloadManifestName;
  if LoadAndValidatePayloadManifest(ManifestPath, Manifest) then
  begin
    Log('Cleaning obsolete app-owned payload after successful install: ' + AppDirectory);
    if not CleanObsoletePayload(AppDirectory, '', True, Manifest) then
      Log('One or more obsolete payload entries could not be removed; the installed payload remains usable.');
  end;
  SHChangeNotify(ShellChangeAssociationChanged, ShellChangeIdList, 0, 0);
  Log('Notified Explorer that installed shortcut and taskbar branding changed.');
end;
