Use this compliance check only immediately before creating a commit, immediately before opening a pull request, or while reviewing a pull request.

Merely loading or including this file in the context must not trigger a full PDF read.

For a commit, inspect the staged changes. For PR creation or review, inspect the complete PR diff. If any `.sql` file anywhere in the repository is added, modified, deleted, or renamed, read `Docs/Zapper - Team_Lead Assignment.pdf` in its entirety and ensure that all SQL changes conform to its applicable specifications, including the data model, payment lifecycle, audit information, and daily reporting requirements.

If no `.sql` files are changed, a full PDF read is not required unless another applicable instruction or trigger requires it.
