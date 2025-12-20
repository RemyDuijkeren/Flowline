# Flowline

![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![NuGet](https://img.shields.io/nuget/dt/RemyDuijkeren.Flowline.svg)](https://www.nuget.org/packages/Flowline)

**Flowline** is a lightweight CLI tool that streamlines the deployment of Power Platform solutions while tracking all
changes in git.

It follows a GitHubFlow-style process to clone, sync, and deploy solutions, offering a flexible alternative to
Microsoft's rigid Power Platform Pipelines. Unlike Microsoft's approach of moving managed solutions across controlled
environments, Flowline gives you the freedom to deploy unmanaged solutions where and how you want — keeping you in
control while maintaining a complete source history in your git repository.

---

## 🚀 Why Flowline?

Power Platform Pipelines only support deploying managed solutions.
Flowline exists to give you a flexible, developer-friendly alternative:

- ✅ Works with **unmanaged solutions**
- ✅ Fits naturally into **GitHubFlow and source control workflows**
- ✅ Simple commands to **clone, sync, and deploy**
- ✅ No locked-down layers, no forced managed-only structures

Flowline is inspired by real flowlines: focused, adaptable, and purpose-built to get your solution from *source* to
*target* — without the unnecessary infrastructure.

Flowline keeps it simple:

- You own your environments.
- You control your source.
- You choose unmanaged.

Flowline — _GitHubFlow pipelines for unmanaged Power Platform solutions._

---

## ⚙️ Install

```bash
dotnet tool install --global Flowline
```

## 🛠️ Commands
```bash
flowline init
```
➡ Clone your Production environment to a new Dev environment (creates a fresh sandbox).

```bash
flowline prime
```
➡ Sync solutions: download from Dev, unpack, and commit into Git.

```bash
flowline export --target prod
```
➡ Deploy your solution to Production (or another target like Test).

🌟 Example usage
```bash
flowline prime --name Dev123 --region europe
flowline export --solution MySolution
flowline stage --solution MySolution
flowline release --solution MySolution
```
## Development

```bash
dotnet pack
dotnet tool uninstall -g Flowline
dotnet tool install -g Flowline --add-source ./artifacts/nupkg --prerelease
```

## Dependencies

```bash
winget install Microsoft.PowerAppsCLI
winget install Git.Git
```
