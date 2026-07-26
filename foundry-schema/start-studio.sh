#!/bin/bash

# Function to clean up background processes on Ctrl+C
cleanup() {
    echo ""
    echo "Stopping Studio Visual IDE servers..."
    kill $BACKEND_PID 2>/dev/null
    kill $STUDIO_PID 2>/dev/null
    exit 0
}

# Trap SIGINT and SIGTERM to run cleanup
trap cleanup SIGINT SIGTERM

export DOTNET_ROOT="/Users/prajuab/.dotnet"
export PATH="$PATH:/Users/prajuab/.dotnet/tools"

echo "Starting Foundry.Schema.Backend Server..."
~/.dotnet/dotnet run --project backend/Foundry.Schema.Backend.csproj --no-build &
BACKEND_PID=$!

echo "Starting Studio Frontend Web App..."
cd studio
npm run dev &
STUDIO_PID=$!
cd ..

# Wait 2 seconds for boot then open browser
sleep 3
open http://localhost:5173/

echo "Foundry.Schema.Studio is running!"
echo "Press Ctrl+C to terminate both servers."

# Wait for both processes to keep script running
wait $BACKEND_PID $STUDIO_PID
