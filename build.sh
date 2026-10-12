#! /usr/bin/env bash
set -e

outputFolder='_output'
testPackageFolder='_tests'

#Artifact variables
artifactsFolder="_artifacts";

ProgressStart()
{
    echo "Start '$1'"
}

ProgressEnd()
{
    echo "Finish '$1'"
}

ResolveBuildBranch()
{
    # What gets recorded as AssemblyConfiguration, which the app reads back
    # as BuildInfo.Branch: the suffix of BuildInfo.Release ("1.2.3-main"),
    # the "release" field of /initialize.json, and the Sentry environment.
    #
    # This used to read ${BUILD_SOURCEBRANCHNAME} and nothing else. That is
    # an Azure Pipelines variable; GitHub Actions never sets it, so every
    # tarball, zip and installer built there reported release "1.2.3-" with
    # an empty branch, while the Docker image -- which passes its branch
    # explicitly as BUILD_BRANCH -- did not.
    #
    # Order: an explicit BUILD_BRANCH (the same name the Dockerfile's ARG
    # uses, so one value can feed both artifact families), then the Azure
    # variable for anyone still on it, then what GitHub Actions provides.
    #
    # A tag is not a branch. Reporting the tag name would repeat the version
    # ("1.2.3-v1.2.3-beta") and mint a new Sentry environment per release,
    # so a tag build reports the branch releases are cut from.
    local branch="${BUILD_BRANCH:-${BUILD_SOURCEBRANCHNAME:-}}"

    if [ -z "$branch" ]; then
        if [ "${GITHUB_REF_TYPE:-}" = "tag" ]; then
            branch="main"
        else
            # GITHUB_REF_NAME is "<number>/merge" on a pull request;
            # GITHUB_HEAD_REF is the branch the PR came from, and is only
            # set there.
            branch="${GITHUB_HEAD_REF:-${GITHUB_REF_NAME:-}}"
        fi
    fi

    # The value lands in a sed replacement and in an XML element, and branch
    # names routinely contain "/" ("librarr/foo", "fix/bar"), which would
    # end the sed expression early and fail the build under `set -e`.
    printf '%s' "$branch" | tr -c 'A-Za-z0-9._-' '-'
}

UpdateVersionNumber()
{
    if [ "$READARRVERSION" != "" ]; then
        echo "Updating Version Info"
        # AssemblyVersion attribute requires strict numeric major[.minor[.build[.revision]]].
        # Strip any semver pre-release suffix like "-beta" / "-rc1" so
        # "1.0.0-beta.4" → "1.0.0.4". The full semver string is still
        # carried in the macOS Info.plist CFBundleShortVersionString below
        # and surfaces in /api/v1/system/status via InformationalVersion.
        ASSEMBLY_VERSION=$(echo "$READARRVERSION" | sed 's/-[A-Za-z][A-Za-z0-9]*//')
        sed -i'' -e "s/<AssemblyVersion>[0-9.*]\+<\/AssemblyVersion>/<AssemblyVersion>$ASSEMBLY_VERSION<\/AssemblyVersion>/g" src/Directory.Build.props
        # With no branch to report (a local build outside CI), leave the
        # "$(Configuration)-dev" placeholder alone rather than blank it.
        #
        # The match is "anything up to the closing tag" rather than the old
        # letters-only class, so a second pass in the same checkout -- the
        # release workflow builds win-x64 and win-x86 in turn -- restamps
        # instead of silently not matching a value that contains digits.
        local buildBranch
        buildBranch=$(ResolveBuildBranch)
        if [ -n "$buildBranch" ]; then
            echo "Branch: $buildBranch"
            sed -i'' -e "s/<AssemblyConfiguration>[^<]*<\/AssemblyConfiguration>/<AssemblyConfiguration>${buildBranch}<\/AssemblyConfiguration>/g" src/Directory.Build.props
        fi
        sed -i'' -e "s/<string>10.0.0.0<\/string>/<string>$READARRVERSION<\/string>/g" distribution/osx/Readarr.app/Contents/Info.plist
    fi
}

EnableExtraPlatformsInSDK()
{
    # Patch the SDK that will actually build -- the one global.json
    # resolves to -- rather than whichever SDK a hard-coded pattern
    # happens to match first. A hosted runner carries several side by
    # side (6.x, 8.x and 10.x have all been present at once), so
    # "find the first one" is not the same question as "find the one
    # doing the build".
    #
    # This used to grep for '6\.\d\.\d+'. After the .NET 10 migration
    # that selected a .NET 6 SDK the build never used, and once CI
    # stopped installing .NET 6 it selected nothing at all, leaving
    # BUNDLEDVERSIONS as a bare "/Microsoft.NETCoreSdk.BundledVersions.props"
    # for sed to fail on -- fatal, because this script runs under set -e.
    SDK_VERSION=$(dotnet --version)
    SDK_PATH=$(dotnet --list-sdks | sed -n "s|^${SDK_VERSION} \[\(.*\)\]$|\1|p" | head -1)

    if [ -z "$SDK_PATH" ]; then
        echo "Could not locate SDK $SDK_VERSION in 'dotnet --list-sdks'" >&2
        exit 1
    fi

    BUNDLEDVERSIONS="${SDK_PATH}/${SDK_VERSION}/Microsoft.NETCoreSdk.BundledVersions.props"

    # Fail rather than skip. This function only runs when extra platforms
    # were explicitly asked for, and quietly not delivering them produces
    # a release that is missing RIDs for no stated reason.
    if [ ! -f "$BUNDLEDVERSIONS" ]; then
        echo "No BundledVersions.props for SDK $SDK_VERSION at $BUNDLEDVERSIONS" >&2
        exit 1
    fi

    # On .NET 10 this is inert, and correctly so. The SDK now ships
    # freebsd-x64 in its own RID list, so the guard below short-circuits
    # and the sed never runs. linux-x86 is genuinely gone -- absent from
    # the list with no runtime pack -- so the sed could not deliver it
    # anyway. Verified against mcr.microsoft.com/dotnet/sdk:10.0.
    #
    # Which means the guard tests only the first of the two RIDs it adds.
    # Left that way deliberately: making it test both would start
    # appending linux-x86 to an SDK that cannot build it. If extra
    # platforms are ever revived, freebsd-x64 needs nothing and linux-x86
    # needs a runtime pack that does not exist -- not a change here.
    if grep -q freebsd-x64 "$BUNDLEDVERSIONS"; then
        echo "Extra platforms already enabled in $BUNDLEDVERSIONS"
    else
        echo "Enabling extra platform support in $BUNDLEDVERSIONS"
        sed -i.ORI 's/osx-x64/osx-x64;freebsd-x64;linux-x86/' "$BUNDLEDVERSIONS"
    fi
}

EnableExtraPlatforms()
{
    # `grep -qv X file` is true when ANY line lacks X, which for a
    # multi-line props file is always true -- including immediately after
    # this function has just added the RIDs. The intended test is "the
    # file does not contain X", so a second invocation appended them
    # again.
    if ! grep -q freebsd-x64 src/Directory.Build.props; then
        sed -i'' -e "s^<RuntimeIdentifiers>\(.*\)</RuntimeIdentifiers>^<RuntimeIdentifiers>\1;freebsd-x64;linux-x86</RuntimeIdentifiers>^g" src/Directory.Build.props
    fi
}

LintUI()
{
    ProgressStart 'ESLint'
    yarn lint
    ProgressEnd 'ESLint'

    ProgressStart 'Stylelint'
    if [ "$os" = "windows" ]; then
        yarn stylelint-windows
    else
        yarn stylelint-linux
    fi
    ProgressEnd 'Stylelint'
}

Build()
{
    ProgressStart 'Build'

    rm -rf $outputFolder
    rm -rf $testPackageFolder

    slnFile=src/Readarr.sln

    if [ $os = "windows" ]; then
        platform=Windows
    else
        platform=Posix
    fi

    if [[ -z "$RID" || -z "$FRAMEWORK" ]];
    then
        dotnet msbuild -restore $slnFile -p:Configuration=Release -p:Platform=$platform -t:PublishAllRids
    else
        # RID stays quoted so a semicolon-separated list survives word
        # splitting. Note this is necessary but NOT sufficient for a
        # multi-RID value: under Git Bash on Windows, MSYS re-marshals
        # arguments containing `;` on the way to a native .exe and msbuild
        # still ends up seeing a stray `win-x86` switch. Pass one RID per
        # invocation on Windows — the release workflow does.
        dotnet msbuild -restore $slnFile -p:Configuration=Release -p:Platform=$platform -p:RuntimeIdentifiers="$RID" -t:PublishAllRids
    fi

    ProgressEnd 'Build'
}

YarnInstall()
{
    ProgressStart 'yarn install'
    yarn install --frozen-lockfile --network-timeout 120000
    ProgressEnd 'yarn install'
}

RunWebpack()
{
    ProgressStart 'Running webpack'
    yarn run build --env production
    ProgressEnd 'Running webpack'
}

PackageFiles()
{
    local folder="$1"
    local framework="$2"
    local runtime="$3"

    rm -rf $folder
    mkdir -p $folder
    cp -r $outputFolder/$framework/$runtime/publish/* $folder
    cp -r $outputFolder/Readarr.Update/$framework/$runtime/publish $folder/Readarr.Update
    # UI is usually built in the same checkout (./build.sh runs both
    # --frontend and --backend) and lives at _output/UI. In CI's
    # split-job shape, the frontend lives in a separate artifact and
    # is stitched in later by the `package` job. Tolerate either.
    if [ -d "$outputFolder/UI" ]; then
        cp -r $outputFolder/UI $folder
    else
        echo "Note: $outputFolder/UI not present — leaving placeholder for CI to populate"
        mkdir -p $folder/UI
    fi

    echo "Adding LICENSE"
    cp LICENSE.md $folder
}

PackageLinux()
{
    local framework="$1"
    local runtime="$2"

    ProgressStart "Creating $runtime Package for $framework"

    local folder=$artifactsFolder/$runtime/$framework/Readarr

    PackageFiles "$folder" "$framework" "$runtime"

    echo "Removing Service helpers"
    rm -f $folder/ServiceUninstall.*
    rm -f $folder/ServiceInstall.*

    echo "Removing Readarr.Windows"
    rm $folder/Readarr.Windows.*

    echo "Adding Readarr.Mono to UpdatePackage"
    cp $folder/Readarr.Mono.* $folder/Readarr.Update
    if [ "$framework" = "net10.0" ]; then
        cp $folder/Mono.Posix.NETStandard.* $folder/Readarr.Update
        cp $folder/libMonoPosixHelper.* $folder/Readarr.Update
    fi

    ProgressEnd "Creating $runtime Package for $framework"
}

PackageMacOS()
{
    local framework="$1"
    local runtime="$2"
    
    ProgressStart "Creating MacOS Package for $framework $runtime"

    local folder=$artifactsFolder/$runtime/$framework/Readarr

    PackageFiles "$folder" "$framework" "$runtime"

    echo "Removing Service helpers"
    rm -f $folder/ServiceUninstall.*
    rm -f $folder/ServiceInstall.*

    echo "Removing Readarr.Windows"
    rm $folder/Readarr.Windows.*

    echo "Adding Readarr.Mono to UpdatePackage"
    cp $folder/Readarr.Mono.* $folder/Readarr.Update
    if [ "$framework" = "net10.0" ]; then
        cp $folder/Mono.Posix.NETStandard.* $folder/Readarr.Update
        cp $folder/libMonoPosixHelper.* $folder/Readarr.Update
    fi

    ProgressEnd 'Creating MacOS Package'
}

PackageMacOSApp()
{
    local framework="$1"
    local runtime="$2"
    
    ProgressStart "Creating macOS App Package for $framework $runtime"

    local folder="$artifactsFolder/$runtime-app/$framework"

    rm -rf $folder
    mkdir -p $folder
    cp -r distribution/osx/Readarr.app $folder
    mkdir -p $folder/Readarr.app/Contents/MacOS

    echo "Copying Binaries"
    cp -r $artifactsFolder/$runtime/$framework/Readarr/* $folder/Readarr.app/Contents/MacOS

    echo "Removing Update Folder"
    rm -r $folder/Readarr.app/Contents/MacOS/Readarr.Update

    ProgressEnd 'Creating macOS App Package'
}

PackageWindows()
{
    local framework="$1"
    local runtime="$2"

    ProgressStart "Creating $runtime Package for $framework"

    local folder=$artifactsFolder/$runtime/$framework/Readarr

    PackageFiles "$folder" "$framework" "$runtime"

    # The Windows-specific TFM (net10.0-windows) publish only exists when
    # a Windows host ran the build. It is present on the release
    # workflow's build-windows job (windows-2022) and on a developer's
    # Windows machine; it is absent on the Linux build-backend job and on
    # macOS. Skip rather than emit an archive missing Readarr.Windows.dll —
    # the package job's loops already WARN+skip missing per-RID outputs.
    local winPublish="$outputFolder/$framework-windows/$runtime/publish"
    if [ ! -d "$winPublish" ]; then
        echo "Note: $winPublish not present — skipping Windows package for $runtime (incomplete, downstream WARN+skip)"
        rm -rf "$folder"
        ProgressEnd "Creating $runtime Package for $framework"
        return 0
    fi
    cp -r "$winPublish"/* $folder

    echo "Removing Readarr.Mono"
    rm -f $folder/Readarr.Mono.*
    rm -f $folder/Mono.Posix.NETStandard.*
    rm -f $folder/libMonoPosixHelper.*

    echo "Adding Readarr.Windows to UpdatePackage"
    cp $folder/Readarr.Windows.* $folder/Readarr.Update

    ProgressEnd "Creating $runtime Package for $framework"
}

Package()
{
    local framework="$1"
    local runtime="$2"
    local SPLIT

    IFS='-' read -ra SPLIT <<< "$runtime"

    case "${SPLIT[0]}" in
        linux|freebsd*)
            PackageLinux "$framework" "$runtime"
            ;;
        win)
            PackageWindows "$framework" "$runtime"
            ;;
        osx)
            PackageMacOS "$framework" "$runtime"
            PackageMacOSApp "$framework" "$runtime"
            ;;
    esac
}

# Path to the ISCC.exe that BuildInstaller should use. Set by InstallInno,
# which either finds one already on the box or unpacks a portable copy.
isccPath=''

FindInstalledIscc()
{
    if [[ -n "$ISCC_PATH" && -f "$ISCC_PATH" ]];
    then
        echo "$ISCC_PATH"
        return
    fi

    local candidate
    for candidate in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
                     "/c/Program Files/Inno Setup 6/ISCC.exe"
    do
        if [ -f "$candidate" ];
        then
            echo "$candidate"
            return
        fi
    done

    command -v ISCC 2>/dev/null || true
}

BuildInstaller()
{
    local framework="$1"
    local runtime="$2"

    ProgressStart "Creating Windows Installer for $runtime"

    "$isccPath" distribution/windows/setup/readarr.iss "//DFramework=$framework" "//DRuntime=$runtime"

    ProgressEnd "Created Windows Installer for $runtime"
}

InstallInno()
{
    # GitHub's windows-2022 runners ship Inno Setup (6.7.1 at time of
    # writing), so use what is already there and skip the download entirely
    # on CI. The download path below still exists for a bare dev machine.
    isccPath=$(FindInstalledIscc)
    if [ -n "$isccPath" ];
    then
        echo "Using installed Inno Setup: $isccPath"
        return
    fi

    ProgressStart "Installing portable Inno Setup"

    rm -rf _inno

    # jrsoftware moved binary distribution to GitHub Releases; the old
    # files.jrsoftware.org/is/6/innosetup-<ver>.exe path now serves only
    # .issig signature files and 404s for every .exe, including the 6.2.0
    # this script used to pin. The tag spells the version with underscores.
    #
    # -f matters as much as the URL. Without it curl wrote the 404 page to
    # innosetup.exe and the next line ran the HTML as a shell script
    # ("syntax error near unexpected token `newline'"), which is a baffling
    # way to find out a download failed.
    local innoVersion="${INNOVERSION:-6.7.3}"
    curl -fsSL --output innosetup.exe \
        "https://github.com/jrsoftware/issrc/releases/download/is-${innoVersion//./_}/innosetup-${innoVersion}.exe"
    mkdir _inno
    ./innosetup.exe //portable=1 //silent //currentuser //dir=.\\_inno
    rm innosetup.exe

    if [ ! -f _inno/ISCC.exe ];
    then
        echo "ERROR: Inno Setup unpacked but _inno/ISCC.exe is missing"
        exit 1
    fi

    isccPath='./_inno/ISCC.exe'

    ProgressEnd "Installed portable Inno Setup"
}

RemoveInno()
{
    rm -rf _inno
}

PackageTests()
{
    local framework="$1"
    local runtime="$2"

    cp test.sh "$testPackageFolder/$framework/$runtime/publish"

    rm -f $testPackageFolder/$framework/$runtime/*.log.config

    ProgressEnd 'Creating Test Package'
}

# Use mono or .net depending on OS
case "$(uname -s)" in
    CYGWIN*|MINGW32*|MINGW64*|MSYS*)
        # on windows, use dotnet
        os="windows"
        ;;
    *)
        # otherwise use mono
        os="posix"
        ;;
esac

POSITIONAL=()

if [ $# -eq 0 ]; then
    echo "No arguments provided, building everything"
    BACKEND=YES
    FRONTEND=YES
    PACKAGES=YES
    INSTALLER=NO
    LINT=YES
    ENABLE_EXTRA_PLATFORMS=NO
    ENABLE_EXTRA_PLATFORMS_IN_SDK=NO
fi

while [[ $# -gt 0 ]]
do
key="$1"

case $key in
    --backend)
        BACKEND=YES
        shift # past argument
        ;;
    --enable-bsd|--enable-extra-platforms)
        ENABLE_EXTRA_PLATFORMS=YES
        shift # past argument
        ;;
    --enable-extra-platforms-in-sdk)
        ENABLE_EXTRA_PLATFORMS_IN_SDK=YES
        shift # past argument
        ;;
    -r|--runtime)
        RID="$2"
        shift # past argument
        shift # past value
        ;;
    -f|--framework)
        FRAMEWORK="$2"
        shift # past argument
        shift # past value
        ;;
    --frontend)
        FRONTEND=YES
        shift # past argument
        ;;
    --packages)
        PACKAGES=YES
        shift # past argument
        ;;
    --installer)
        INSTALLER=YES
        shift # past argument
        ;;
    --lint)
        LINT=YES
        shift # past argument
        ;;
    --all)
        BACKEND=YES
        FRONTEND=YES
        PACKAGES=YES
        LINT=YES
        shift # past argument
        ;;
    *)    # unknown option
        POSITIONAL+=("$1") # save it in an array for later
        shift # past argument
        ;;
esac
done
set -- "${POSITIONAL[@]}" # restore positional parameters

if [ "$ENABLE_EXTRA_PLATFORMS_IN_SDK" = "YES" ];
then
    EnableExtraPlatformsInSDK
fi

if [ "$BACKEND" = "YES" ];
then
    UpdateVersionNumber
    if [ "$ENABLE_EXTRA_PLATFORMS" = "YES" ];
    then
        EnableExtraPlatforms
    fi
    Build
    if [[ -z "$RID" || -z "$FRAMEWORK" ]];
    then
        PackageTests "net10.0" "win-x64"
        PackageTests "net10.0" "win-x86"
        PackageTests "net10.0" "linux-x64"
        PackageTests "net10.0" "linux-musl-x64"
        PackageTests "net10.0" "osx-x64"
        if [ "$ENABLE_EXTRA_PLATFORMS" = "YES" ];
        then
            PackageTests "net10.0" "freebsd-x64"
            PackageTests "net10.0" "linux-x86"
        fi
    else
        PackageTests "$FRAMEWORK" "$RID"
    fi
fi

if [[ "$LINT" = "YES" || "$FRONTEND" = "YES" ]];
then
    YarnInstall
fi

if [ "$LINT" = "YES" ];
then
    LintUI
fi

if [ "$FRONTEND" = "YES" ];
then
    RunWebpack
fi

if [ "$PACKAGES" = "YES" ];
then
    UpdateVersionNumber

    if [[ -z "$RID" || -z "$FRAMEWORK" ]];
    then
        Package "net10.0" "win-x64"
        Package "net10.0" "win-x86"
        Package "net10.0" "linux-x64"
        Package "net10.0" "linux-musl-x64"
        Package "net10.0" "linux-arm64"
        Package "net10.0" "linux-musl-arm64"
        Package "net10.0" "linux-arm"
        Package "net10.0" "linux-musl-arm"
        Package "net10.0" "osx-x64"
        Package "net10.0" "osx-arm64"
        if [ "$ENABLE_EXTRA_PLATFORMS" = "YES" ];
        then
            Package "net10.0" "freebsd-x64"
            Package "net10.0" "linux-x86"
        fi
    else
        Package "$FRAMEWORK" "$RID"
    fi
fi

if [ "$INSTALLER" = "YES" ];
then
    InstallInno
    BuildInstaller "net10.0" "win-x64"
    BuildInstaller "net10.0" "win-x86"
    RemoveInno
fi
