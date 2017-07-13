#!/usr/bin/env bash

echo "#############################################################"
echo "Restore"
echo "#############################################################"
dotnet restore WilsonLinux.sln

echo "#############################################################"
echo "Build"
echo "#############################################################"
dotnet build WilsonLinux.sln

echo "#############################################################"
echo "Pack"
echo "#############################################################"
dotnet pack WilsonLinux.sln

# copy all nuget packages to artifacts folder
echo "#############################################################"
echo "Move nuget packages to artifacts folder"
echo "#############################################################"
rm -rf artifacts
mkdir artifacts
mv src/*/bin/Debug/*.nupkg artifacts/