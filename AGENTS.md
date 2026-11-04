# Main Global Instructions

1. Never commit without a specific instruction to do so.
2. Never push to `main`.
3. Never merge a pull request.
4. When an instruction or requirement is unclear, ask for clarification instead of making assumptions.
5. During partial development, do not overengineer unspecified paths or create mock data unless specifically requested. For unfinished work, use `NotImplementedException` with an accompanying comment that begins with `//TODO:`.
6. Keep the README.md and the SOLUTION.md file small and suitable for human consumption. If these files are changed validate their content against the instructions in `Docs/Zapper - Team_Lead Assignment.pdf`
7. Store AI-created lists, progress files, decision documents, and similar artifacts in the repository's `/Docs` directory. Prompt the user and obtain approval before creating any such file.
8. When the user asked to add something to the TODO list, it should be added to /Docs/TODO.md. This file is for todo notes for the user and should not be modified by agents unless explicitly asked. Notes should be short points of items that we need to come back to.
9. When the user asked to add something to the notes list, it should be added to /Docs/Notes.md. This file is for general notes and assumptions for the user and should not be modified by agents unless explicitly asked.

## Additional Paths and Links

Before creating a commit, opening a pull request, or reviewing a pull request, read and follow [ai/skills/AcmeCompliance.md](ai/skills/AcmeCompliance.md) and [ai/skills/SqlCompliance.md](ai/skills/SqlCompliance.md).
