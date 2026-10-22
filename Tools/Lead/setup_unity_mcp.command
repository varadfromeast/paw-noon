#!/bin/bash
# Paw Noon: one-time setup of Unity's official CLI + MCP server so Claude can drive the Editor natively
# (run C#, recompile, play, screenshots, hierarchy) instead of the file bridge.
# Double-click this file; it opens in Terminal. Safe to re-run. Log: ~/Documents/ChatGPT/paw/PawNoonPixel/Lead/mcp_setup.log
PROJECT="$HOME/Documents/ChatGPT/paw/PawNoonPixel"
LOG="$PROJECT/Lead/mcp_setup.log"
mkdir -p "$PROJECT/Lead"
exec > >(tee "$LOG") 2>&1
echo "== Paw Noon Unity MCP setup $(date) =="
for d in "$HOME/.unity/bin" "$HOME/.local/bin" "/usr/local/bin" "/opt/homebrew/bin"; do [ -x "$d/unity" ] && export PATH="$d:$PATH"; done
hash -r

if ! command -v unity >/dev/null 2>&1; then
  echo "== 0/3 Installing the Unity CLI (official installer, beta channel) =="
  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh -o "$PROJECT/Lead/unity_cli_install.sh" || { echo "FAILED: could not download the installer"; exit 1; }
  UNITY_CLI_CHANNEL=beta bash "$PROJECT/Lead/unity_cli_install.sh" || { echo "FAILED: installer exited with an error"; exit 1; }
  for d in "$HOME/.unity/bin" "$HOME/.local/bin" "/usr/local/bin" "/opt/homebrew/bin"; do [ -x "$d/unity" ] && export PATH="$d:$PATH"; done
  hash -r
fi
command -v unity >/dev/null 2>&1 || { echo "FAILED: unity not on PATH after install (check $LOG)"; exit 1; }

echo "== Unity CLI: $(unity --version --no-banner 2>/dev/null) =="
echo "== 1/3 Adding the Pipeline package (com.unity.pipeline) to the project =="
unity pipeline install "$PROJECT" --non-interactive --no-banner || unity pipeline install --project-path "$PROJECT" --non-interactive --no-banner
echo "== 2/3 Registering the Unity MCP server in the Claude desktop app =="
unity mcp configure claude --project-path "$PROJECT" --yes --no-banner
unity mcp configure claude-code --project-path "$PROJECT" --yes --no-banner 2>/dev/null || true
echo "== 3/3 Done =="
echo "SETUP_OK"
echo "Next: quit and reopen the Claude desktop app so it starts the Unity MCP server. This window can be closed."
