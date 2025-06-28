# Power Flow (mimics GitHub Flow) for Power Platform
# https://docs.github.com/en/get-started/using-github/github-flow
# https://i0.wp.com/build5nines.com/wp-content/uploads/2018/01/GitHub-Flow.png?resize=1024%2C353&ssl=1

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("clone", "sync", "push-to-test", "merge", "delete-env")]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [string]$Environment
)

$ErrorActionPreference = "Stop"

function Assert-PacCliInstalled {
    $pac = Get-Command pac -ErrorAction SilentlyContinue
    if (-not $pac) {
        Write-Error "The Power Platform CLI (pac) is not installed or not in PATH. Please install it: https://aka.ms/pac"
        exit 1
    }
}

function Assert-GitInstalled {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) {
        Write-Error "🦑 GIT NOT FOUND! Your commit powers are weak, young padawan. Please install Git from https://git-scm.com/ before you can join the commit side."
        exit 1
    }
}

function Get-PartsFromEnvUrl {
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvUrl
    )

    if ($EnvUrl -match '^https://([^.]+)\.([^.]+\.[^.]+\.[a-z]+)(?:/|$)') {
        $envDomain = $matches[1]           # 'instancename'
        $regionDomain = $matches[2]         # 'crm4.dynamics.com'
    } else {
        Write-Error "Could not extract environment name and region domain from URL: $($EnvUrl)"
        exit 1
    }

    $regionDomainToRegion = @{
        "crm.dynamics.com"           = "unitedstates"
        "crm3.dynamics.com"          = "canada"
        "crm2.dynamics.com"          = "southamerica"
        "crm4.dynamics.com"          = "europe"
        "crm12.dynamics.com"         = "france"
        "crm.microsoftdynamics.de"   = "germany"
        "crm21.dynamics.com"         = "switzerland"
        "crm11.dynamics.com"         = "unitedkingdom"
        "crm22.dynamics.com"         = "norway"
        "crm5.dynamics.com"          = "asia"
        "crm6.dynamics.com"          = "japan"
        "crm8.dynamics.com"          = "australia"
        "crm9.dynamics.com"          = "india"
        "crm20.dynamics.com"         = "uae"
        "crm19.dynamics.com"         = "korea"
        "crm.dynamics.cn"            = "china"
        "crm.appsplatform.us"        = "usgovhigh"
    }

    if ($regionDomainToRegion.ContainsKey($regionDomain)) {
        return [PSCustomObject]@{
            EnvDomain    = $envDomain
            RegionDomain = $regionDomain
            Region       = $regionDomainToRegion[$regionDomain]
        }
    } else {
        Write-Error "Unknown region/domain: $regionDomain"
        exit 1
    }
}

Write-Host "Running command '$Command' for environment '$Environment'..."

Assert-PacCliInstalled
Assert-GitInstalled

switch ($Command) {
    "clone" { # aka Clone/pull and Branch
        Write-Host "Validating '$Environment'..."
        
        # TODO: Also allow to Clone Test and Acceptance env from Production env?

        $sourceEnv = pac admin list --json | ConvertFrom-Json | Where-Object { $_.EnvironmentUrl -match $Environment }
        if ($null -eq $sourceEnv) {
            Write-Error "Source Environment not found."
            exit 1
        }
        # Write-Host "Source Environment: $sourceEnv"

        if ($sourceEnv.Type -ne 'Production') {
            Write-Error "Source environment type must be 'Production'. Found: '$($sourceEnv.Type)'. Aborting."
            exit 1
        }

        $urlParts = Get-PartsFromEnvUrl -EnvUrl $sourceEnv.EnvironmentUrl
        # Write-Host "Determined UrlParts: $urlParts"
    
        $targetName = "$prodName Dev"
        $targetEnvDomain = "$($urlParts.EnvDomain)-dev"
        $targetUrl = "https://$targetEnvDomain.$($urlParts.RegionDomain)/"

        $targetEnv = pac admin list --json | ConvertFrom-Json | Where-Object { $_.EnvironmentUrl -eq $targetUrl }
        if ($null -ne $targetEnv) {
            Write-Host "Target Environment already exists: $($targetEnv.EnvironmentUrl)"
            $overwrite = Read-Host "Do you want to overwrite it? (yes/no)"
            if ($overwrite -ne 'yes') {
                Write-Host "Aborting operation."
                exit 0
            }
            Write-Host "Overwriting existing environment..."
        } else {
            Write-Host "Creating environment $targetUrl..."
            pac admin create --name "$targetName (cloning)" --domain $targetDomain --region $region --type Sandbox
        }

        $targetEnv = pac admin list --json | ConvertFrom-Json | Where-Object { $_.EnvironmentUrl -eq $targetUrl }
        if ($null -eq $targetEnv) {
            Write-Error "Target Environment not found."
            exit 1
        }
        # Write-Host "Target Environment: $targetEnv"

        Write-Host "Copy '$Environment' to '$($targetEnv.EnvironmentUrl)'..."
        # pac admin copy --name $targetName --source-env $sourceEnv.EnvironmentUrl --target-env $targetEnv.EnvironmentUrl --type FullCopy #MinimalCopy

        Write-Host "All done!"
    }
    "sync" { # aka Commit
        # 1. Publish all customizations

        # 2. Clone or Sync from Dev to local solution folder
        $solutionName = "Cr07982"
        $gitRemoteUrl = "https://github.com/AutomateValue/Dataverse01.git"
        $commitMessage = "Commit changes to solution '$solutionName' in environment '$Environment'"
        $rootFolder = Get-Location # get the current working directory
        $srcSolutionFolder = "$rootFolder/src/$solutionName"
        $cdsprojPath = Join-Path $srcSolutionFolder "$solutionName.cdsproj"

        # 1. Clone Git repo if not already a Git repo
        if (-not (Test-Path "$rootFolder/.git")) {
            Write-Host "No repository found. Cloning..."
            git clone $gitRemoteUrl $rootFolder # clone the remote repository to the local folder
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to clone the repository. Please check the URL and your network connection."
                exit 1
            }
        }

        if (-not (Test-Path $cdsprojPath)){
            Write-Host "No solution folder for '$solutionName' found. Cloning..."

            if (Test-Path $srcSolutionFolder) {
                Write-Host "Removing existing solution folder..."
                Remove-Item $srcSolutionFolder -Recurse -Force
            }

            pac solution clone --name $solutionName --environment $Environment --packagetype Unmanaged --outputDirectory "$rootFolder/src"
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to clone the solution. Please check the environment and solution name."
                exit 1
            }
        } else {
            Write-Host "The solution folder for '$solutionName' exists! Syncing it..."
            pac solution sync --solution-folder $srcSolutionFolder --environment $Environment --packagetype Unmanaged    
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to sync the solution. Please check the environment and solution name."
                exit 1
            }
        }

        Write-Host "Building Solution '$solutionName'..."
        dotnet build $srcSolutionFolder --output "$rootFolder/artifacts/"

        Write-Host "Committing changes to local repository..."
        git add -A # add all files
        git commit -m $commitMessage

        # 5. Push changes to remote repository
        git push

        Write-Host "All done!"
    }
    "push-to-test" { # aka PullRequest
        Write-Host "Pushing changes to test environment..."
        # 1. Check if test exists
        # 2a. If not, create it and copy from Prod
        # 2b. If yes, should we copy from Prod again???
        # 3. Export solution from Dev

        # 4. Import solution to test
        # 5. Publish all customizations
    }
    "merge" {
        Write-Host "Merge pull request into master..."
        # Add logic here
    }
    "delete-env" {
        Write-Host "Deleting environment..."
        # Add logic here
    }
}

#$who = pac auth who --json | ConvertFrom-Json
#$geo = ($who | Where-Object { $_.Key -eq 'Environment Geo:' }).Value
#Write-Host "Environment Geo: $geo"