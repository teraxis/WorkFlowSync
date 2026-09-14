.PHONY: setup dev test lint typecheck e2e reset publish

setup:
	dotnet restore WorkFlowSync.sln

dev:
	dotnet run --project src/WorkFlowSync.App

test:
	dotnet test WorkFlowSync.sln --nologo

lint:
	dotnet format WorkFlowSync.sln --verify-no-changes

typecheck:
	dotnet build WorkFlowSync.sln --nologo -warnaserror

e2e:
	@echo "No UI; end-to-end checks run against a temp folder pair (see docs/testing.md)"

publish:
	dotnet publish src/WorkFlowSync.App/WorkFlowSync.App.csproj -c Release -r win-x64 -o publish/portable --nologo
	dotnet publish src/WorkFlowSync.Cli/WorkFlowSync.Cli.csproj -c Release -r win-x64 -o publish/portable --nologo

reset:
	dotnet clean WorkFlowSync.sln --nologo
