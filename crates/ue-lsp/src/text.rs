//! Position arithmetic between LSP coordinates and byte offsets.
//!
//! LSP addresses text as a zero-based line plus a **UTF-16 code unit** column,
//! while [`ue_parser::UnrealSymbol`] records a one-based line and a byte range.
//! Everything that crosses that boundary goes through here, so the conversion
//! is in one place and can be tested without standing up a server.

use tower_lsp::lsp_types::{Position, Range};

/// Byte offsets of the start of every line in a document.
///
/// Built once per document revision so repeated position lookups stay O(log n)
/// in the line count instead of rescanning the whole buffer each request.
#[derive(Debug, Clone)]
pub struct LineIndex {
    starts: Vec<usize>,
    ends: Vec<usize>,
    len: usize,
}

impl Default for LineIndex {
    fn default() -> Self {
        Self::new("")
    }
}

impl LineIndex {
    pub fn new(text: &str) -> Self {
        let mut starts = vec![0usize];
        let mut ends = Vec::new();
        for (offset, byte) in text.bytes().enumerate() {
            if byte == b'\n' {
                let end = if offset > 0 && text.as_bytes()[offset - 1] == b'\r' {
                    offset - 1
                } else {
                    offset
                };
                ends.push(end);
                starts.push(offset + 1);
            }
        }
        ends.push(text.len());
        LineIndex {
            starts,
            ends,
            len: text.len(),
        }
    }

    /// Number of lines, counting a trailing empty line after a final newline.
    pub fn line_count(&self) -> usize {
        self.starts.len()
    }

    /// Byte range of `line` (zero-based), excluding the line terminator.
    pub fn line_range(&self, line: usize) -> Option<(usize, usize)> {
        let start = *self.starts.get(line)?;
        let end = *self.ends.get(line)?;
        Some((start, end.max(start)))
    }

    /// LSP position to byte offset.
    ///
    /// A column past the end of the line clamps to the line end rather than
    /// failing: editors routinely send a caret one past the last character, and
    /// refusing those would drop valid completion requests.
    pub fn offset(&self, text: &str, position: Position) -> Option<usize> {
        let (start, end) = self.line_range(position.line as usize)?;
        let line = text.get(start..end)?;

        let mut utf16 = 0u32;
        for (byte_offset, ch) in line.char_indices() {
            if utf16 >= position.character {
                return Some(start + byte_offset);
            }
            utf16 += ch.len_utf16() as u32;
        }
        Some(end)
    }

    /// Byte offset to LSP position. Offsets past the end clamp to the last line.
    pub fn position(&self, text: &str, offset: usize) -> Position {
        let mut offset = offset.min(self.len).min(text.len());
        while !text.is_char_boundary(offset) {
            offset -= 1;
        }
        let line = match self.starts.binary_search(&offset) {
            Ok(exact) => exact,
            Err(next) => next.saturating_sub(1),
        };
        let start = self.starts[line];
        offset = offset.min(self.ends[line]);
        let character = text
            .get(start..offset)
            .map(|slice| slice.chars().map(|c| c.len_utf16() as u32).sum())
            .unwrap_or(0);
        Position {
            line: line as u32,
            character,
        }
    }

    /// Range covering a whole one-based source line, as symbols record them.
    ///
    /// Used when a stored symbol only carries a line number — pointing a client
    /// at the whole line is honest about the resolution we actually have.
    pub fn line_as_range(&self, text: &str, one_based_line: usize) -> Range {
        let line = one_based_line.saturating_sub(1);
        match self.line_range(line) {
            Some((start, end)) => Range {
                start: self.position(text, start),
                end: self.position(text, end),
            },
            None => {
                let zero = Position {
                    line: 0,
                    character: 0,
                };
                Range {
                    start: zero,
                    end: zero,
                }
            }
        }
    }
}

/// Whether `byte` can appear in a C++ identifier.
fn is_ident(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || byte == b'_'
}

/// The identifier surrounding `offset`, with its byte range.
///
/// A caret sitting immediately *after* an identifier counts as being on it,
/// which is what hover and go-to-definition need when the client reports the
/// position the user clicked at the end of a word.
pub fn word_at(text: &str, offset: usize) -> Option<(&str, usize, usize)> {
    let bytes = text.as_bytes();
    let offset = offset.min(bytes.len());

    let on_word = offset < bytes.len() && is_ident(bytes[offset]);
    let after_word = offset > 0 && is_ident(bytes[offset - 1]);
    if !on_word && !after_word {
        return None;
    }

    let mut start = offset;
    while start > 0 && is_ident(bytes[start - 1]) {
        start -= 1;
    }
    let mut end = offset;
    while end < bytes.len() && is_ident(bytes[end]) {
        end += 1;
    }
    if start == end {
        return None;
    }
    Some((&text[start..end], start, end))
}

/// The identifier fragment immediately before `offset`, which is what the user
/// has typed so far. Empty when the caret follows a non-identifier character.
pub fn prefix_before(text: &str, offset: usize) -> &str {
    let bytes = text.as_bytes();
    let offset = offset.min(bytes.len());
    if !text.is_char_boundary(offset) {
        return "";
    }
    let mut start = offset;
    while start > 0 && is_ident(bytes[start - 1]) {
        start -= 1;
    }
    &text[start..offset]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_index_addresses_an_empty_document() {
        let index = LineIndex::default();
        assert_eq!(index.line_count(), 1);
        assert_eq!(index.position("", 0), Position::new(0, 0));
        assert_eq!(index.offset("", Position::new(0, 0)), Some(0));
    }

    #[test]
    fn windows_line_terminators_are_not_editable_columns() {
        let source = "ab\r\ncd\r\n";
        let index = LineIndex::new(source);
        assert_eq!(index.line_range(0), Some((0, 2)));
        assert_eq!(index.offset(source, Position::new(0, 99)), Some(2));
        assert_eq!(index.position(source, 3), Position::new(0, 2));
        assert_eq!(index.line_as_range(source, 2).end, Position::new(1, 2));
    }

    #[test]
    fn partial_utf8_offsets_do_not_panic_or_lose_preceding_columns() {
        let source = "abc🐧Name";
        let index = LineIndex::new(source);
        assert_eq!(index.position(source, 5), Position::new(0, 3));
        assert_eq!(prefix_before(source, 5), "");
        assert_eq!(word_at(source, 5), None);
    }

    #[test]
    fn offsets_round_trip_through_positions() {
        let text = "UCLASS()\nclass AMyActor\n{\n};\n";
        let index = LineIndex::new(text);

        for offset in 0..=text.len() {
            let position = index.position(text, offset);
            assert_eq!(
                index.offset(text, position),
                Some(offset),
                "offset {offset}"
            );
        }
    }

    #[test]
    fn columns_are_utf16_code_units_not_bytes() {
        // The emoji is four UTF-8 bytes but two UTF-16 code units, so a caret
        // after it is column 2 in LSP terms and byte 4 in ours.
        let text = "\u{1F427}X";
        let index = LineIndex::new(text);

        let after_emoji = Position {
            line: 0,
            character: 2,
        };
        assert_eq!(index.offset(text, after_emoji), Some(4));
        assert_eq!(index.position(text, 4), after_emoji);
    }

    #[test]
    fn column_past_end_of_line_clamps() {
        let text = "ab\ncd";
        let index = LineIndex::new(text);
        let far = Position {
            line: 0,
            character: 99,
        };
        assert_eq!(index.offset(text, far), Some(2));
    }

    #[test]
    fn line_as_range_uses_one_based_numbering() {
        let text = "first\nsecond\nthird";
        let index = LineIndex::new(text);
        let range = index.line_as_range(text, 2);
        assert_eq!(
            range.start,
            Position {
                line: 1,
                character: 0
            }
        );
        assert_eq!(
            range.end,
            Position {
                line: 1,
                character: 6
            }
        );
    }

    #[test]
    fn word_at_covers_both_edges() {
        let text = "int Health = 0;";
        assert_eq!(word_at(text, 4).map(|w| w.0), Some("Health"));
        assert_eq!(word_at(text, 7).map(|w| w.0), Some("Health"));
        // Caret immediately after the last character still resolves the word.
        assert_eq!(word_at(text, 10).map(|w| w.0), Some("Health"));
        // On the space between tokens there is no word.
        assert_eq!(word_at(text, 11).map(|w| w.0), None);
    }

    #[test]
    fn prefix_stops_at_non_identifier_characters() {
        assert_eq!(prefix_before("UPROPERTY(Edit", 14), "Edit");
        assert_eq!(prefix_before("UPROPERTY(", 10), "");
        assert_eq!(prefix_before("AMy", 3), "AMy");
    }
}
