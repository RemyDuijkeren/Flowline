# Flowline CLI

![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/RemyDuijkeren.OpenTelemetry.Instrumentation.DataverseServiceClient)
[![NuGet](https://img.shields.io/nuget/dt/RemyDuijkeren.Flowline.svg)](https://www.nuget.org/packages/Flowline)

**Flowline** is the lightweight deployment CLI for unmanaged Power Platform solutions.
It helps you follow a GitHubFlow-style process to clone, sync, and deploy solutions — without the rigidity of Microsoft
Power Platform Pipelines, and without forcing managed solutions.

Flowline is your lightweight, flexible alternative to rigid Power Platform Pipelines.
While Microsoft’s pipelines move managed solutions across controlled environments, Flowline gives you freedom to move
unmanaged solutions where and how you want — always in control, always in source.

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

---

## ⚙️ Install

```bash
dotnet tool install --global Flowline
```

## 🛠️ Commands
```bash
flowline clone
```
➡ Clone your Production environment to a new Dev environment (creates a fresh sandbox).

```bash
flowline sync
```
➡ Sync solutions: download from Dev, unpack, and commit into Git.

```bash
flowline deploy --target prod
```
➡ Deploy your solution to Production (or another target like Test).

🌟 Example usage
```bash
flowline clone --name Dev123 --region europe
flowline sync --solution MySolution
flowline deploy --target prod --solution MySolution
```

## 📌 Philosophy
Flowline keeps it simple:

- You own your environments.
- You control your source.
- You choose unmanaged.

Flowline — _GitHubFlow pipelines for unmanaged Power Platform solutions._
