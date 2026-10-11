"""A small C# lexer shared by the lint scripts: it knows comments, string literals (plain, verbatim, interpolated) and char literals, so a lint never matches code inside them."""
import re

def mask(src):
    """Return src with comments and the CONTENT of string/char literals replaced by spaces (newlines kept), so offsets and line numbers stay valid. The quotes stay."""
    out = list(src)
    i, n = 0, len(src)
    def blank(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '
    while i < n:
        c = src[i]
        two = src[i:i + 2]
        if two == '//':
            j = src.find('\n', i)
            j = n if j < 0 else j
            blank(i, j); i = j
        elif two == '/*':
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            blank(i, j); i = j
        elif c == '"' or (c in '@$' and src[i + 1:i + 2] in ('"', '@', '$') and re.match(r'[@$]{1,2}"', src[i:i + 3])):
            m = re.match(r'([@$]{0,2})"', src[i:i + 3])
            prefix = m.group(1)
            verbatim = '@' in prefix
            j = i + len(prefix) + 1
            start = j
            while j < n:
                if verbatim:
                    if src[j] == '"':
                        if src[j + 1:j + 2] == '"':
                            j += 2; continue
                        break
                else:
                    if src[j] == '\\':
                        j += 2; continue
                    if src[j] == '"':
                        break
                j += 1
            blank(start, j); i = j + 1
        elif c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            blank(i + 1, j); i = j + 1
        else:
            i += 1
    return ''.join(out)

def line_of(src, pos):
    return src.count('\n', 0, pos) + 1

def match_brace(masked, open_pos):
    depth = 0
    for k in range(open_pos, len(masked)):
        if masked[k] == '{':
            depth += 1
        elif masked[k] == '}':
            depth -= 1
            if depth == 0:
                return k
    return -1
