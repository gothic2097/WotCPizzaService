# Copilot Instructions

## Project Guidelines
- Leave at least one empty line between functions. Keep style-only changes separate from logic changes.
- Always use braces for if/else statements and loops, even for a single-statement body. Use multiline braces for control-flow statements (if/else, for/foreach, while, etc.) and separate adjacent control-flow statements/blocks with an empty line.
- Expand non-empty code blocks (functions, control flow, try/catch, and lambdas) with braces on separate lines. This rule does not apply to brace initializers; leave empty {} unchanged.
- Do not inline non-empty brace-delimited code blocks. Put opening and closing braces on their own lines, with the block contents on separate indented lines.
- Format enums, classes, and structs as multiline declarations with opening and closing braces on separate lines and one enum value or data member per line, rather than compact single-line declarations. Keep these style changes separate from logic changes.