import os
import json
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("powershell.exe") or shutil.which("powershell")
ISCC_CANDIDATES = [
    Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/Inno Setup 6/ISCC.exe",
    Path(os.environ.get("ProgramFiles(x86)", "")) / "Inno Setup 6/ISCC.exe",
    Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/Inno Setup 7/ISCC.exe",
    Path(os.environ.get("ProgramFiles(x86)", "")) / "Inno Setup 7/ISCC.exe",
]


def run_powershell(script: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            POWERSHELL,
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            script,
        ],
        cwd=ROOT,
        text=True,
        capture_output=True,
    )


def evaluate_installer_flavor(script: str, local_qa: bool) -> dict[str, object]:
    active = True
    conditional_stack: list[tuple[bool, bool, bool]] = []
    defines: dict[str, str] = {}
    defined_symbols = {"LocalQaArtifact"} if local_qa else set()
    setup: dict[str, str] = {}
    icons: list[str] = []
    cleanup_include_count = 0
    section = ""

    def resolve(value: str) -> str:
        for key, replacement in defines.items():
            value = value.replace("{#" + key + "}", replacement)
        return value

    for raw_line in script.splitlines():
        line = raw_line.strip()
        conditional = re.fullmatch(r"#if(n?)def\s+(\w+)", line)
        if conditional:
            condition = conditional.group(2) in defined_symbols
            if conditional.group(1):
                condition = not condition
            conditional_stack.append((active, condition, False))
            active = active and condition
            continue
        if line == "#else" and conditional_stack:
            parent_active, condition, saw_else = conditional_stack[-1]
            if saw_else:
                raise AssertionError("Duplicate Inno #else")
            conditional_stack[-1] = (parent_active, condition, True)
            active = parent_active and not condition
            continue
        if line == "#endif" and conditional_stack:
            active, _, _ = conditional_stack.pop()
            continue
        if line.startswith("#if") or line in ("#else", "#endif"):
            raise AssertionError(f"Unsupported or unmatched Inno conditional: {line}")
        if not active:
            continue
        line = resolve(line)
        if line.startswith("#emit"):
            raise AssertionError(f"Unsupported Inno output directive: {line}")
        if line.startswith("#include"):
            if line != '#include "UpgradeCleanup.iss"':
                raise AssertionError(f"Unsupported Inno include: {line}")
            cleanup_include_count += 1
            continue
        undefine = re.fullmatch(r"#undef\s+(\w+)", line)
        if undefine:
            defines.pop(undefine.group(1), None)
            defined_symbols.discard(undefine.group(1))
            continue
        define = re.fullmatch(r'#define\s+(\w+)\s+"([^"]*)"', line)
        if define:
            defines[define.group(1)] = resolve(define.group(2))
            defined_symbols.add(define.group(1))
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line
            continue
        if section == "[Setup]" and "=" in line:
            key, value = line.split("=", 1)
            setup[key] = resolve(value)
        elif section == "[Icons]" and line.startswith("Name:"):
            icons.append(resolve(line))

    if conditional_stack:
        raise AssertionError("Unclosed Inno conditional")

    return {
        "name": setup["AppName"],
        "app_id": setup["AppId"],
        "directory": setup["DefaultDirName"].rsplit("\\", 1)[-1],
        "output": setup["OutputBaseFilename"],
        "default_directory": setup["DefaultDirName"],
        "default_group": setup["DefaultGroupName"],
        "icons": icons,
        "cleanup_include_count": cleanup_include_count,
    }


def powershell_ast_records(path: Path) -> list[dict[str, object]]:
    escaped_path = str(path).replace("'", "''")
    result = run_powershell(
        f"""
        $tokens=$null; $errors=$null
        $ast=[System.Management.Automation.Language.Parser]::ParseFile('{escaped_path}',[ref]$tokens,[ref]$errors)
        if($errors.Count){{ throw ($errors | Out-String) }}
        $nodes=$ast.FindAll({{
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -or
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -or
            $node -is [System.Management.Automation.Language.ReturnStatementAst] -or
            $node -is [System.Management.Automation.Language.ExitStatementAst] -or
            $node -is [System.Management.Automation.Language.ThrowStatementAst]
        }},$true)
        $records=@($nodes | ForEach-Object {{
            $conditions=@(); $scopes=@(); $tryDepth=0; $tryBodies=0; $tryStart=$null; $tryHasPopFinally=$false; $parent=$_.Parent
            while($parent){{
                if($parent -is [System.Management.Automation.Language.IfStatementAst]){{
                    $matched=$false
                    foreach($clause in $parent.Clauses){{
                        $body=$clause.Item2.Extent
                        if($_.Extent.StartOffset -ge $body.StartOffset -and $_.Extent.EndOffset -le $body.EndOffset){{
                            $conditions += "if ($($clause.Item1.Extent.Text))"
                            $matched=$true
                            break
                        }}
                    }}
                    if(-not $matched -and $parent.ElseClause){{
                        $body=$parent.ElseClause.Extent
                        if($_.Extent.StartOffset -ge $body.StartOffset -and $_.Extent.EndOffset -le $body.EndOffset){{
                            $conditions += "else"
                        }}
                    }}
                }}
                if($parent -is [System.Management.Automation.Language.TryStatementAst]){{
                    $tryDepth++
                    $body=$parent.Body.Extent
                    if($_.Extent.StartOffset -ge $body.StartOffset -and $_.Extent.EndOffset -le $body.EndOffset){{
                        $tryBodies++; $tryStart=$parent.Extent.StartOffset
                        $directPops=@($parent.Finally.FindAll({{param($candidate)
                            $candidate -is [System.Management.Automation.Language.CommandAst] -and
                            $candidate.GetCommandName() -ieq 'Pop-Location' -and
                            $candidate.Parent.Parent -eq $parent.Finally
                        }},$true))
                        $tryHasPopFinally=[bool]($parent.Finally.Statements.Count -eq 1 -and $directPops.Count -eq 1)
                    }}
                    else {{ $scopes += "CatchOrFinally" }}
                }}
                if($parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                   $parent -is [System.Management.Automation.Language.FunctionMemberAst] -or
                   $parent -is [System.Management.Automation.Language.TypeDefinitionAst] -or
                   $parent -is [System.Management.Automation.Language.DataStatementAst] -or
                   $parent -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -or
                   $parent -is [System.Management.Automation.Language.SubExpressionAst] -or
                   $parent -is [System.Management.Automation.Language.LoopStatementAst] -or
                   $parent -is [System.Management.Automation.Language.SwitchStatementAst] -or
                   $parent -is [System.Management.Automation.Language.TrapStatementAst]){{
                    $scopes += $parent.GetType().Name
                }}
                $parent=$parent.Parent
            }}
            $strings=@($_.FindAll({{param($child) $child -is [System.Management.Automation.Language.StringConstantExpressionAst]}},$true) | ForEach-Object {{$_.Value}})
            $variables=@($_.FindAll({{param($child) $child -is [System.Management.Automation.Language.VariableExpressionAst]}},$true) | ForEach-Object {{$_.VariablePath.UserPath}})
            $nestedCode=@($_.FindAll({{param($child) $child -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -or $child -is [System.Management.Automation.Language.SubExpressionAst]}},$true)).Count
            [pscustomobject]@{{
                Kind=$_.GetType().Name
                CommandName=$(if($_ -is [System.Management.Automation.Language.CommandAst]){{$_.GetCommandName()}}else{{$null}})
                Target=$(if($_ -is [System.Management.Automation.Language.AssignmentStatementAst]){{$_.Left.Extent.Text}}else{{$null}})
                TargetName=$(if($_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left -is [System.Management.Automation.Language.VariableExpressionAst]){{$_.Left.VariablePath.UserPath}}else{{$null}})
                Text=$_.Extent.Text
                Start=$_.Extent.StartOffset
                Conditions=@($conditions)
                Scopes=@($scopes)
                TryDepth=$tryDepth
                TryBodies=$tryBodies
                TryStart=$tryStart
                TryHasPopFinally=$tryHasPopFinally
                Strings=@($strings)
                Variables=@($variables)
                NestedCode=$nestedCode
            }}
        }})
        ConvertTo-Json -InputObject $records -Depth 5 -Compress
        """
    )
    if result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    records = json.loads(result.stdout)
    return records if isinstance(records, list) else [records]


class RuntimeAssetValidationTests(unittest.TestCase):
    def setUp(self):
        if not POWERSHELL:
            self.skipTest("Windows PowerShell is unavailable")

    def test_local_qa_installer_identity_is_distinct_and_release_gate_exercises_flavor(self):
        installer = (ROOT / "installer/MHC.Invoicing.iss").read_text(encoding="utf-8")
        cleanup_include = (ROOT / "installer/UpgradeCleanup.iss").read_text(encoding="utf-8")
        self.assertNotRegex(cleanup_include, r"(?m)^\s*#")
        self.assertNotIn("{#", cleanup_include)
        self.assertNotRegex(cleanup_include, r"(?im)^\s*\[(Setup|Icons)\]\s*$")
        production = evaluate_installer_flavor(installer, local_qa=False)
        qa = evaluate_installer_flavor(installer, local_qa=True)

        self.assertEqual(
            {
                "name": "MHC Invoices V4",
                "app_id": "{{94DDA1A1-673E-4EBD-AD76-337F150024B4}",
                "directory": "MHC Invoices V4",
                "output": "MHC-Invoices-V4-Setup-x64-Unsigned",
                "default_directory": r"{localappdata}\Programs\MHC Technology\MHC Invoices V4",
                "default_group": "MHC Invoices V4",
                "cleanup_include_count": 1,
            },
            {key: production[key] for key in production if key != "icons"},
        )
        self.assertEqual(
            {
                "name": "MHC Invoices V4 - LOCAL QA",
                "app_id": "{{E8161B82-C5AA-4D64-A1E7-CA1071C79891}",
                "directory": "MHC Invoices V4 Local QA",
                "output": "MHC-Invoices-V4-Setup-x64-LocalQA",
                "default_directory": r"{localappdata}\Programs\MHC Technology\MHC Invoices V4 Local QA",
                "default_group": "MHC Invoices V4 - LOCAL QA",
                "cleanup_include_count": 1,
            },
            {key: qa[key] for key in qa if key != "icons"},
        )
        expected_production_icons = [
            r'Name: "{group}\MHC Invoices V4"; Filename: "{app}\MHC.Invoicing.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"; IconIndex: 0',
            r'Name: "{autodesktop}\MHC Invoices V4"; Filename: "{app}\MHC.Invoicing.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"; IconIndex: 0; Tasks: desktopicon',
        ]
        expected_qa_icons = [icon.replace("MHC Invoices V4", "MHC Invoices V4 - LOCAL QA") for icon in expected_production_icons]
        self.assertEqual(expected_production_icons, production["icons"])
        self.assertEqual(expected_qa_icons, qa["icons"])
        self.assertEqual(1, production["cleanup_include_count"])
        self.assertEqual(1, qa["cleanup_include_count"])

        records = powershell_ast_records(ROOT / "build/Build-Release.ps1")
        self.assertFalse(
            any(record["Kind"] in ("ReturnStatementAst", "ExitStatementAst") for record in records),
            "The release script must not contain return or exit statements in any scope.",
        )
        commands = [record for record in records if record["Kind"] == "CommandAst"]
        assignments = [record for record in records if record["Kind"] == "AssignmentStatementAst"]
        development_condition = ["if ($UseDevelopmentSigning)"]
        release_try_starts = {
            record["TryStart"]
            for record in records
            if record["TryBodies"] == 1 and record["TryHasPopFinally"] and not record["Scopes"]
        }
        self.assertEqual(1, len(release_try_starts))
        release_try_start = next(iter(release_try_starts))

        def assert_live(record: dict[str, object], conditions: list[str]) -> None:
            self.assertEqual(conditions, record["Conditions"])
            self.assertEqual([], record["Scopes"])
            self.assertEqual(1, record["TryDepth"])
            self.assertEqual(1, record["TryBodies"])
            self.assertEqual(release_try_start, record["TryStart"])
            self.assertTrue(record["TryHasPopFinally"])
            self.assertEqual(0, record["NestedCode"])

        def normalized_name(value: str | None) -> str:
            return (value or "").split("\\")[-1].split(":")[-1].casefold()

        def command(
            strings: list[str],
            variables: list[str],
            conditions: list[str],
        ) -> dict[str, object]:
            matches = [
                record for record in commands
                if normalized_name(record["CommandName"]) == "invoke-checked"
                and record["Strings"] == strings
                and record["Variables"] == variables
            ]
            self.assertEqual(1, len(matches), strings)
            assert_live(matches[0], conditions)
            return matches[0]

        test_records = [
            command(
                ["Invoke-Checked", "dotnet", "test", project, *arguments],
                ["Configuration"],
                development_condition,
            )
            for project, arguments in (
                (
                    "tests/MHC.Invoicing.Infrastructure.Tests/MHC.Invoicing.Infrastructure.Tests.csproj",
                    ["-c", "-p:LocalQaArtifact=true", "--no-restore", "--filter", "FullyQualifiedName~AppDataPathsTests", "-v", "minimal"],
                ),
                (
                    "tests/MHC.Invoicing.Application.Tests/MHC.Invoicing.Application.Tests.csproj",
                    ["-c", "-p:LocalQaArtifact=true", "--no-restore", "--filter", "FullyQualifiedName~TemporaryPdfFileTests", "-v", "minimal"],
                ),
                (
                    "tests/MHC.Invoicing.Ui.Tests/MHC.Invoicing.Ui.Tests.csproj",
                    ["-c", "-p:Platform=x64", "-p:LocalQaArtifact=true", "--no-restore", "--filter", "FullyQualifiedName~LocalizationResourceTests", "-v", "minimal"],
                ),
            )
        ]
        packaging = command(
            ["Invoke-Checked", "python", "tests/packaging/test_release_packaging.py"],
            [],
            [],
        )
        publish = command(["Invoke-Checked", "dotnet"], ["publishArguments"], [])
        iscc = command(["Invoke-Checked"], ["iscc", "isccArguments"], [])
        self.assertLess(max(record["Start"] for record in test_records), packaging["Start"])
        self.assertLess(packaging["Start"], publish["Start"])
        self.assertLess(publish["Start"], iscc["Start"])

        self.assertFalse(
            any(
                normalized_name(record["CommandName"] or (record["Strings"][0] if record["Strings"] else ""))
                in ("set-variable", "sv")
                for record in commands
            ),
            "Critical release variables must not be mutated through Set-Variable.",
        )
        critical_targets = {"usedevelopmentsigning", "publisharguments", "isccarguments", "setupbasename", "setup"}
        critical_assignments = [
            record for record in assignments
            if normalized_name(record["TargetName"]) in critical_targets
        ]
        self.assertEqual(
            {"publisharguments": 2, "isccarguments": 2, "setupbasename": 2, "setup": 1},
            {
                target: sum(normalized_name(record["TargetName"]) == target for record in critical_assignments)
                for target in critical_targets
                if any(normalized_name(record["TargetName"]) == target for record in critical_assignments)
            },
        )

        def assignment(text: str, conditions: list[str]) -> dict[str, object]:
            matches = [record for record in critical_assignments if record["Text"] == text]
            self.assertEqual(1, len(matches), text)
            assert_live(matches[0], conditions)
            return matches[0]

        publish_production = assignment(
            '$publishArguments = @("publish", $project, "-c", $Configuration, "-p:Platform=x64", "-r", "win-x64", "--self-contained", "true", "--no-restore", "-o", $publish, "-v", "minimal")',
            [],
        )
        publish_qa = assignment('$publishArguments += "-p:LocalQaArtifact=true"', development_condition)
        production_iscc = assignment("$isccArguments = @($innoScript)", [])
        production_name = assignment('$setupBaseName = "MHC-Invoices-V4-Setup-x64-Unsigned"', [])
        qa_iscc = assignment('$isccArguments = @("/DLocalQaArtifact=1", $innoScript)', development_condition)
        qa_name = assignment('$setupBaseName = "MHC-Invoices-V4-Setup-x64-LocalQA"', development_condition)
        setup_path = assignment('$setup = Join-Path $installerDirectory "$setupBaseName.exe"', [])

        self.assertLess(publish_production["Start"], publish_qa["Start"])
        self.assertLess(publish_qa["Start"], publish["Start"])
        self.assertLess(production_iscc["Start"], qa_iscc["Start"])
        self.assertLess(production_name["Start"], qa_name["Start"])
        self.assertLess(qa_iscc["Start"], iscc["Start"])
        self.assertLess(qa_name["Start"], iscc["Start"])
        self.assertLess(iscc["Start"], setup_path["Start"])

    def test_stale_xbf_and_app_pri_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix="mhc-packaging-") as temporary:
            fixture = Path(temporary)
            project = fixture / "project"
            output = fixture / "output"
            (project / "Pages").mkdir(parents=True)
            (output / "Pages").mkdir(parents=True)
            (project / "App.xaml").write_text("<Application />", encoding="utf-8")
            (project / "Pages/Current.xaml").write_text("<Page />", encoding="utf-8")
            for relative in ("App.xbf", "Pages/Current.xbf", "MHC.Invoicing.pri"):
                path = output / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"current")
            (output / "Microsoft.UI.pri").write_bytes(b"framework")
            (output / "Pages/RemovedPage.xbf").write_bytes(b"stale")
            (output / "RemovedFeature.pri").write_bytes(b"stale")

            helper = ROOT / "build/Release-Packaging.ps1"
            command = (
                f". '{helper}'; "
                f"Assert-AppRuntimeAssets -ProjectDirectory '{project}' "
                f"-BuildOutput '{output}'"
            )
            result = run_powershell(command)

            self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
            diagnostics = result.stdout + result.stderr
            self.assertIn("Pages\\RemovedPage.xbf", diagnostics)
            self.assertIn("RemovedFeature.pri", diagnostics)

    def test_exact_runtime_asset_set_is_accepted(self):
        with tempfile.TemporaryDirectory(prefix="mhc-packaging-") as temporary:
            fixture = Path(temporary)
            project = fixture / "project"
            output = fixture / "output"
            (project / "Pages").mkdir(parents=True)
            (output / "Pages").mkdir(parents=True)
            (project / "App.xaml").write_text("<Application />", encoding="utf-8")
            (project / "Pages/Current.xaml").write_text("<Page />", encoding="utf-8")
            for relative in ("App.xbf", "Pages/Current.xbf", "MHC.Invoicing.pri"):
                path = output / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"current")
            (output / "Microsoft.UI.pri").write_bytes(b"framework")

            helper = ROOT / "build/Release-Packaging.ps1"
            command = (
                f". '{helper}'; "
                f"Assert-AppRuntimeAssets -ProjectDirectory '{project}' "
                f"-BuildOutput '{output}'"
            )
            result = run_powershell(command)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_payload_manifest_exactly_lists_the_installed_file_set(self):
        with tempfile.TemporaryDirectory(prefix="mhc-packaging-") as temporary:
            publish = Path(temporary) / "publish"
            (publish / "Pages").mkdir(parents=True)
            (publish / "MHC.Invoicing.exe").write_bytes(b"app")
            (publish / "Pages/Invoice.xbf").write_bytes(b"xbf")

            helper = ROOT / "build/Release-Packaging.ps1"
            command = (
                f". '{helper}'; "
                f"Write-AppPayloadManifest -PublishDirectory '{publish}'"
            )
            result = run_powershell(command)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(
                [
                    ".mhc-payload-manifest.txt",
                    "MHC.Invoicing.exe",
                    "Pages\\Invoice.xbf",
                ],
                (publish / ".mhc-payload-manifest.txt").read_text(encoding="utf-8-sig").splitlines(),
            )


class UpgradeCleanupTests(unittest.TestCase):
    def setUp(self):
        self.iscc = next((candidate for candidate in ISCC_CANDIDATES if candidate.is_file()), None)

    def run_setup(self, setup: Path) -> subprocess.CompletedProcess[str]:
        try:
            return subprocess.run(
                [str(setup), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
                text=True,
                capture_output=True,
                timeout=60,
            )
        except OSError as error:
            if getattr(error, "winerror", None) in (4551, 4556):
                self.skipTest("Application Control blocks locally compiled Inno Setup fixtures")
            raise


    def test_cleanup_is_deferred_until_post_install(self):
        script = (ROOT / "installer/UpgradeCleanup.iss").read_text(encoding="utf-8")

        self.assertNotIn("PrepareToInstall", script)
        self.assertIn("CurStepChanged", script)
        self.assertIn("ssPostInstall", script)

    def test_failed_real_upgrade_leaves_previous_executable_and_payload_usable(self):
        if self.iscc is None:
            self.skipTest("Inno Setup compiler is unavailable")

        with tempfile.TemporaryDirectory(prefix="mhc-inno-failed-upgrade-") as temporary:
            fixture = Path(temporary)
            install = fixture / "installed"
            output = fixture / "out"
            install.mkdir()
            (install / "app.cmd").write_text("@echo previous-app-works\n", encoding="ascii")
            (install / "legacy-payload.dat").write_bytes(b"previous-usable-payload")
            (install / ".mhc-payload-manifest.txt").write_text(
                "app.cmd\nlegacy-payload.dat\n.mhc-payload-manifest.txt\n",
                encoding="utf-8",
            )
            before = subprocess.run(
                [os.environ["COMSPEC"], "/d", "/c", str(install / "app.cmd")],
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, before.returncode, before.stdout + before.stderr)
            self.assertIn("previous-app-works", before.stdout)

            new_source = fixture / "new-app.cmd"
            new_source.write_text("@echo replacement-app\n", encoding="ascii")
            manifest = fixture / ".mhc-payload-manifest.txt"
            manifest.write_text("app.cmd\n.mhc-payload-manifest.txt\n", encoding="utf-8")
            failure_marker = fixture / "intentional-failure-reached.txt"
            cleanup_include = (ROOT / "installer/UpgradeCleanup.iss").as_posix()
            fixture_iss = fixture / "failed-upgrade.iss"
            fixture_iss.write_text(
                textwrap.dedent(
                    f"""
                    [Setup]
                    AppId={{{{ED3554EE-6C43-4E3E-A853-FA5B38964AA5}}}}
                    AppName=MHC Failed Upgrade Fixture
                    AppVersion=2.0
                    DefaultDirName={install.as_posix()}
                    PrivilegesRequired=lowest
                    DisableProgramGroupPage=yes
                    Uninstallable=no
                    OutputDir={output.as_posix()}
                    OutputBaseFilename=failed-upgrade
                    ArchitecturesAllowed=x64compatible

                    [Files]
                    Source: "{new_source.as_posix()}"; DestDir: "{{app}}"; DestName: "app.cmd"; Flags: ignoreversion
                    Source: "{manifest.as_posix()}"; DestDir: "{{app}}"; DestName: ".mhc-payload-manifest.txt"

                    [Code]
                    function PrepareToInstall(var NeedsRestart: Boolean): String;
                    begin
                      SaveStringToFile('{failure_marker.as_posix()}', 'failure reached', False);
                      Result := 'Intentional upgrade failure before file replacement.';
                    end;

                    #include "{cleanup_include}"
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )
            compile_result = subprocess.run(
                [str(self.iscc), str(fixture_iss)], text=True, capture_output=True
            )
            self.assertEqual(0, compile_result.returncode, compile_result.stdout + compile_result.stderr)
            setup = output / "failed-upgrade.exe"
            upgrade_result = self.run_setup(setup)

            self.assertTrue(failure_marker.is_file(), upgrade_result.stdout + upgrade_result.stderr)
            after = subprocess.run(
                [os.environ["COMSPEC"], "/d", "/c", str(install / "app.cmd")],
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, after.returncode, after.stdout + after.stderr)
            self.assertIn("previous-app-works", after.stdout)
            self.assertEqual(
                b"previous-usable-payload", (install / "legacy-payload.dat").read_bytes()
            )

    def test_real_inno_upgrade_removes_obsolete_payload_only_inside_app_root(self):
        if self.iscc is None:
            self.skipTest("Inno Setup compiler is unavailable")

        with tempfile.TemporaryDirectory(prefix="mhc-inno-cleanup-") as temporary:
            fixture = Path(temporary)
            install = fixture / "installed"
            outside = fixture / "runtime-user-data.db"
            output = fixture / "out"
            source = fixture / "new-payload.txt"
            manifest = fixture / ".mhc-payload-manifest.txt"
            install.mkdir()
            (install / "obsolete.dll").write_bytes(b"old")
            (install / "OldFeature").mkdir()
            (install / "OldFeature/removed.xbf").write_bytes(b"old")
            (install / "unins000.exe").write_bytes(b"keep-uninstaller")
            outside.write_bytes(b"keep-user-data")
            source.write_bytes(b"new")
            manifest.write_text(
                "current.txt\n.mhc-payload-manifest.txt\n", encoding="utf-8"
            )

            cleanup_include = (ROOT / "installer/UpgradeCleanup.iss").as_posix()
            fixture_iss = fixture / "cleanup-fixture.iss"
            fixture_iss.write_text(
                textwrap.dedent(
                    f"""
                    [Setup]
                    AppId={{{{B4DD456C-B37A-48D0-A33C-F5A27986271B}}}}
                    AppName=MHC Cleanup Fixture
                    AppVersion=1.0
                    DefaultDirName={install.as_posix()}
                    PrivilegesRequired=lowest
                    DisableProgramGroupPage=yes
                    Uninstallable=no
                    OutputDir={output.as_posix()}
                    OutputBaseFilename=cleanup-fixture
                    ArchitecturesAllowed=x64compatible

                    [Files]
                    Source: "{source.as_posix()}"; DestDir: "{{app}}"; DestName: "current.txt"
                    Source: "{manifest.as_posix()}"; DestDir: "{{app}}"; DestName: ".mhc-payload-manifest.txt"

                    #include "{cleanup_include}"
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )

            compile_result = subprocess.run(
                [str(self.iscc), str(fixture_iss)], text=True, capture_output=True
            )
            self.assertEqual(0, compile_result.returncode, compile_result.stdout + compile_result.stderr)
            setup = output / "cleanup-fixture.exe"
            install_result = self.run_setup(setup)
            self.assertEqual(0, install_result.returncode, install_result.stdout + install_result.stderr)
            self.assertFalse((install / "obsolete.dll").exists())
            self.assertFalse((install / "OldFeature").exists())
            self.assertEqual(b"new", (install / "current.txt").read_bytes())
            self.assertEqual(b"keep-uninstaller", (install / "unins000.exe").read_bytes())
            self.assertEqual(b"keep-user-data", outside.read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
