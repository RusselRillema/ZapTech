# SQL Compliance

## SQL Naming

Apply these naming conventions when creating or changing SQL objects and their application callers. The grouping pattern and `snake_case` style are repository conventions compatible with [PostgreSQL 18 identifier rules](https://www.postgresql.org/docs/18/sql-syntax-lexical.html#SQL-SYNTAX-IDENTIFIERS), not naming patterns mandated by PostgreSQL.

- Use unquoted lowercase `snake_case` for new SQL identifiers, including procedure and parameter names. PostgreSQL folds unquoted identifiers to lowercase; quoted identifiers are case-sensitive.
- Group stored procedures by a shared entity or feature prefix followed by the action: `<group>_<action>`. Use plural entity names consistently, e.g., `payments_create`, `payments_get`, `payments_update`, and `payments_get_for_update`. Related procedures must use the same prefix so they sort together. Multiword groups and actions also use underscores, e.g., `payment_events_create` and `payment_events_get_pending`.
- Translate examples such as `paymentsCreate` and `paymentsGet` to `payments_create` and `payments_get`.
- Name procedure parameters with the `p_` prefix and a descriptive lowercase `snake_case` name, e.g., `p_payment_id` or `p_amount`. Use those exact names when calling procedures with named parameters.
- Schema-qualify procedure definitions and calls, e.g., `public.payments_create` and `public.payments_get`.
- Use ASCII letters, digits, and underscores, beginning with a letter, and avoid reserved keywords. Keep each identifier within PostgreSQL's default limit of 63 bytes to prevent truncation.
- When referencing existing database objects, use their exact declared names and required quoting, including the existing quoted mixed-case table and column names.

## Commit and Pull Request Compliance

Use this compliance check only immediately before creating a commit, immediately before opening a pull request, or while reviewing a pull request.

Merely loading or including this file in the context must not trigger a full PDF read.

For a commit, inspect the staged changes. For PR creation or review, inspect the complete PR diff. If any `.sql` file anywhere in the repository is added, modified, deleted, or renamed, read `Docs/Zapper - Team_Lead Assignment.pdf` in its entirety and ensure that all SQL changes conform to its applicable specifications, including the data model, payment lifecycle, audit information, and daily reporting requirements.

If no `.sql` files are changed, a full PDF read is not required unless another applicable instruction or trigger requires it.
