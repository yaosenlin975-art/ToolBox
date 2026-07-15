#!/bin/bash
cd /mnt/d/Workspaces/ToolBox/Project
dotnet build 2>&1 | tail -5
