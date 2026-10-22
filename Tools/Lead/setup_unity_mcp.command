#!/bin/bash
# Paw Noon: one-time setup of Unity's official MCP server so Claude can drive the Editor natively
# (run C#, recompile, play, screenshots, hierarchy) instead of the file bridge.
# Double-click this file; it opens in Terminal. Safe to re-run.
PROJECT="$HOME/Documents/ChatGPT/paw/PawNoonPixel"
for d in "$HOME/.unity/bin" "$HOME/.local/bin" "/usr/local/bin" "/opt/homebrew/bin"; do [ -x "$d/unity" ] && export PATH="$d:$PATH"; done
hash -r

if ! command -v unity >/dev/null 2>&1; then
  echo "Unity CLI is not installed yet. Install it with Unity's official one-liner (docs.unity.com/cli), then double-click this file again:"
  echo
  echo "  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash"
  echo
  read -n 1 -s -r -p "Press any key to close."
  exit 1
fi

echo "== Unity CLI: $(unity --version --no-banner) =="
echo "== 1/3 Adding the Pipeline package (com.unity.pipeline) to the project =="
unity pipeline install "$PROJECT" --non-interactive || unity pipeline install --project-path "$PROJECT" --non-interactive
echo "== 2/3 Registering the Unity MCP server in the Claude desktop app =="
unity mcp configure claude --project-path "$PROJECT" --yes
unity mcp configure claude-code --project-path "$PROJECT" --yes 2>/dev/null || true
echo "== 3/3 Done =="
echo
echo "Next: 1) Let Unity finish importing com.unity.pipeline (it does so by itself)."
echo "      2) Quit and reopen the Claude desktop app so it starts the Unity MCP server."
echo "      3) Tell Claude: 'unity mcp is set up'."
read -n 1 -s -r -p "Press any key to close."
