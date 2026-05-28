# init_superpowers.ps1
# Script to automate Superpowers initialization for Antigravity

$SkillDir = "$env:USERPROFILE\.gemini\extensions\superpowers\skills"
$AgentsFile = "E:\MVS\projects\WhisperVoice\AGENTS.md"

if (Test-Path $SkillDir) {
    Write-Host "Found Superpowers skills at $SkillDir"
    # Example: you could read the skills and append their summaries to AGENTS.md
    # Or, in a CI/CD context or custom IDE plugin, inject them into the agent's context.
    Write-Host "In the future, this script can dynamically compile SKILL.md contents and prepend them to the agent's system prompt or workspace context."
    
    # List available skills
    Get-ChildItem -Path $SkillDir -Directory | ForEach-Object {
        Write-Host "Available Skill: $($_.Name)"
    }
} else {
    Write-Host "Superpowers skills directory not found."
}
