## Persian / RTL response formatting

When responding in Persian or mixed Persian-English text:

1. Prefer Persian prose, but keep all code, commands, filenames, package names, URLs, variables, and English identifiers inside backticks.
2. Use Markdown structure: headings, bullet points, and fenced code blocks.
3. Never put Persian explanation and long code/commands on the same line.
4. For terminal commands, always use fenced code blocks with `bash`.
5. For code, always use fenced code blocks with the correct language.
6. Keep English technical terms isolated with backticks, for example: `React`, `useEffect`, `package.json`, `npm install`.
7. Avoid inline tables for Persian text.
8. If a response contains both Persian and English, write each item as:
   - Persian explanation first
   - English/code token separately in backticks
9. Do not output raw mixed RTL/LTR paragraphs when code, paths, URLs, or commands are involved.