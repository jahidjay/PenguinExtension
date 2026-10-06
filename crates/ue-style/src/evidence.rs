//! Small lexical evidence filter, not another C++ parser. The existing parser
//! supplies symbol identity; this only proves that an exact source range is a
//! complete macro and (for naming) that a simple declaration is adjacent.

use std::ops::Range;
use ue_parser::{SymbolKind, UnrealSymbol};

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Word,
    Literal,
    Punct(char),
    Boundary,
}

struct Token {
    kind: Kind,
    range: Range<usize>,
}

pub(crate) struct Source<'a> {
    text: &'a str,
    tokens: Vec<Token>,
    pairs: Vec<Option<usize>>,
    parents: Vec<Option<usize>>,
}

pub(crate) struct Invocation<'a> {
    flags: Vec<&'a str>,
    /// Token index after the closing parenthesis.
    pub end: usize,
}

impl Invocation<'_> {
    pub fn has_flag(&self, name: &str) -> bool {
        self.flags
            .iter()
            .any(|flag| flag.eq_ignore_ascii_case(name))
    }
}

impl<'a> Source<'a> {
    pub fn new(text: &'a str) -> Self {
        let tokens = lex(text);
        let mut pairs = vec![None; tokens.len()];
        let mut stack = Vec::new();
        let mut parents = vec![None; tokens.len()];
        for (i, token) in tokens.iter().enumerate() {
            parents[i] = stack.last().copied();
            match token.kind {
                Kind::Punct('(' | '[' | '{') => stack.push(i),
                Kind::Punct(close @ (')' | ']' | '}')) => {
                    if let Some(open) = stack.pop() {
                        let matches = matches!(
                            (tokens[open].kind, close),
                            (Kind::Punct('('), ')')
                                | (Kind::Punct('['), ']')
                                | (Kind::Punct('{'), '}')
                        );
                        if matches {
                            pairs[open] = Some(i);
                        } else {
                            stack.clear();
                        }
                    }
                }
                _ => {}
            }
        }
        Self {
            text,
            tokens,
            pairs,
            parents,
        }
    }

    fn word(&self, i: usize) -> Option<&'a str> {
        let token = self.tokens.get(i)?;
        (token.kind == Kind::Word).then(|| &self.text[token.range.clone()])
    }

    fn punct(&self, i: usize, c: char) -> bool {
        self.tokens.get(i).is_some_and(|t| t.kind == Kind::Punct(c))
    }

    pub fn invocation(&self, symbol: &UnrealSymbol) -> Option<Invocation<'a>> {
        let i = self
            .tokens
            .binary_search_by_key(&symbol.byte_range.start, |t| t.range.start)
            .ok()?;
        // A reflection-looking token inside another call/attribute may only be
        // an argument to an unknown macro. Do not assume expansion semantics.
        let mut parent = self.parents[i];
        while let Some(index) = parent {
            if matches!(self.tokens[index].kind, Kind::Punct('(' | '[')) {
                return None;
            }
            parent = self.parents[index];
        }
        if self.word(i)? != symbol.macro_name || !self.punct(i + 1, '(') {
            return None;
        }
        let close = self.pairs[i + 1]?;
        if self.tokens[close].range.end != symbol.byte_range.end {
            return None;
        }
        let mut flags = Vec::new();
        let mut pos = i + 2;
        while pos < close {
            let key = self.word(pos)?;
            if !key.chars().next()?.is_alphabetic() && !key.starts_with('_') {
                return None;
            }
            pos += 1;
            if self.punct(pos, '=') {
                pos += 1;
                let token = self.tokens.get(pos)?;
                match token.kind {
                    Kind::Word | Kind::Literal => pos += 1,
                    Kind::Punct('(' | '[' | '{') => {
                        let end = self.pairs[pos]?;
                        if end >= close
                            || self.tokens[pos..=end]
                                .iter()
                                .any(|t| t.kind == Kind::Boundary)
                        {
                            return None;
                        }
                        pos = end + 1;
                    }
                    _ => return None,
                }
            } else {
                flags.push(key);
            }
            if pos == close {
                break;
            }
            if !self.punct(pos, ',') {
                return None;
            }
            pos += 1;
            // A dangling comma while typing is not enough evidence.
            if pos == close {
                return None;
            }
        }
        Some(Invocation {
            flags,
            end: close + 1,
        })
    }

    pub fn declaration_name(&self, symbol: &UnrealSymbol, mut pos: usize) -> Option<Range<usize>> {
        match symbol.kind {
            SymbolKind::Struct | SymbolKind::Enum => {
                let keyword = if symbol.kind == SymbolKind::Struct {
                    "struct"
                } else {
                    "enum"
                };
                if self.word(pos)? != keyword {
                    return None;
                }
                pos += 1;
                if symbol.kind == SymbolKind::Enum
                    && matches!(self.word(pos), Some("class" | "struct"))
                {
                    pos += 1;
                }
                if self.word(pos).is_some_and(is_export_macro) {
                    pos += 1;
                }
                if self.word(pos)? != symbol.name {
                    return None;
                }
                let name = self.tokens[pos].range.clone();
                pos += 1;
                if symbol.kind == SymbolKind::Struct && self.word(pos) == Some("final") {
                    pos += 1;
                }
                // Only simple inheritance/enum underlying types after a colon
                // are accepted. Never search past a statement or macro call.
                if self.punct(pos, ':') {
                    pos += 1;
                    self.word(pos)?;
                    while !self.punct(pos, '{') {
                        let t = self.tokens.get(pos)?;
                        if !matches!(t.kind, Kind::Word | Kind::Punct(':' | ',')) {
                            return None;
                        }
                        pos += 1;
                    }
                }
                if !self.punct(pos, '{') {
                    return None;
                }
                let end = self.pairs[pos]?;
                self.punct(end + 1, ';').then_some(name)
            }
            SymbolKind::Property => {
                while matches!(self.word(pos), Some("const" | "volatile")) {
                    pos += 1;
                }
                if self.word(pos)? != "bool" {
                    return None;
                }
                pos += 1;
                if self.word(pos)? != symbol.name {
                    return None;
                }
                let name = self.tokens[pos].range.clone();
                pos += 1;
                // Explicit scalar bool only: not an array, callable, pointer,
                // alias, wrapper, multi-declaration, or unevaluated expression.
                let initialized =
                    self.punct(pos, '=') && matches!(self.word(pos + 1), Some("true" | "false"));
                let bitfield = self.punct(pos, ':') && self.word(pos + 1) == Some("1");
                if initialized || bitfield {
                    pos += 2;
                }
                self.punct(pos, ';').then_some(name)
            }
            _ => None,
        }
    }
}

fn is_export_macro(word: &str) -> bool {
    word.ends_with("_API")
        && word
            .bytes()
            .all(|b| b.is_ascii_uppercase() || b.is_ascii_digit() || b == b'_')
}

fn lex(text: &str) -> Vec<Token> {
    let bytes = text.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        let start = i;
        if bytes[i].is_ascii_whitespace() {
            i += 1;
            continue;
        }
        if text[i..].starts_with("//") || bytes[i] == b'#' {
            let directive = bytes[i] == b'#';
            i = logical_line_end(bytes, i);
            if directive {
                out.push(Token {
                    kind: Kind::Boundary,
                    range: start..i,
                });
            }
            continue;
        }
        if text[i..].starts_with("/*") {
            if let Some(end) = text[i + 2..].find("*/") {
                i += end + 4;
            } else {
                break;
            }
            continue;
        }
        let raw_prefix = ["u8R\"", "uR\"", "UR\"", "LR\"", "R\""]
            .iter()
            .find(|prefix| text[i..].starts_with(**prefix));
        if let Some(prefix) = raw_prefix {
            let delimiter_start = i + prefix.len();
            let Some(open) = text[delimiter_start..]
                .find('(')
                .map(|n| delimiter_start + n)
            else {
                break;
            };
            let delimiter = &text[delimiter_start..open];
            if delimiter.len() > 16
                || delimiter
                    .bytes()
                    .any(|b| b.is_ascii_whitespace() || matches!(b, b')' | b'\\'))
            {
                break;
            }
            let closing = format!("){delimiter}\"");
            let Some(end) = text[open + 1..].find(&closing) else {
                break;
            };
            i = open + 1 + end + closing.len();
            out.push(Token {
                kind: Kind::Literal,
                range: start..i,
            });
            continue;
        }
        let quote_prefix = [
            "u8\"", "u\"", "U\"", "L\"", "\"", "u8'", "u'", "U'", "L'", "'",
        ]
        .iter()
        .find(|prefix| text[i..].starts_with(**prefix));
        if let Some(prefix) = quote_prefix {
            let Some(end) = literal_end(bytes, i + prefix.len() - 1) else {
                break;
            };
            i = end;
            out.push(Token {
                kind: Kind::Literal,
                range: start..i,
            });
            continue;
        }
        let c = text[i..]
            .chars()
            .next()
            .expect("in bounds at a character boundary");
        if c.is_alphanumeric() || c == '_' {
            i += c.len_utf8();
            while i < bytes.len() {
                let next = text[i..].chars().next().expect("in bounds");
                if next.is_alphanumeric() || next == '_' {
                    i += next.len_utf8();
                } else if c.is_ascii_digit()
                    && next == '\''
                    && bytes.get(i + 1).is_some_and(u8::is_ascii_alphanumeric)
                {
                    i += 1;
                } else {
                    break;
                }
            }
            out.push(Token {
                kind: Kind::Word,
                range: start..i,
            });
        } else {
            i += c.len_utf8();
            out.push(Token {
                kind: Kind::Punct(c),
                range: start..i,
            });
        }
    }
    out
}

fn logical_line_end(bytes: &[u8], mut i: usize) -> usize {
    while i < bytes.len() {
        if bytes[i] == b'\n' {
            let before = if i > 0 && bytes[i - 1] == b'\r' {
                i - 1
            } else {
                i
            };
            if before == 0 || bytes[before - 1] != b'\\' {
                return i + 1;
            }
        }
        i += 1;
    }
    i
}

fn literal_end(bytes: &[u8], quote: usize) -> Option<usize> {
    let mut i = quote + 1;
    while i < bytes.len() {
        match bytes[i] {
            b if b == bytes[quote] => return Some(i + 1),
            b'\n' | b'\r' => return None,
            b'\\' => {
                i += 1;
                if bytes.get(i) == Some(&b'\r') && bytes.get(i + 1) == Some(&b'\n') {
                    i += 1;
                }
                i += 1;
            }
            _ => i += 1,
        }
    }
    None
}
