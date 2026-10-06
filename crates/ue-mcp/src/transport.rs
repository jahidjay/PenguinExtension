//! Official SDK newline transport, with an input budget ahead of its line buffer.
use std::{
    io,
    pin::Pin,
    task::{Context, Poll},
};
use tokio::io::{AsyncRead, ReadBuf};

/// Large enough for bounded AI context plus JSON escaping, but not arbitrary streams.
pub const MAX_MESSAGE_BYTES: usize = 512 * 1024;

pub(crate) struct LimitedReader<R> {
    inner: R,
    line_bytes: usize,
}
impl<R> LimitedReader<R> {
    pub(crate) fn new(inner: R) -> Self {
        Self {
            inner,
            line_bytes: 0,
        }
    }
}
impl<R: AsyncRead + Unpin> AsyncRead for LimitedReader<R> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        match Pin::new(&mut self.inner).poll_read(cx, buf) {
            Poll::Ready(Ok(())) => {
                for byte in &buf.filled()[before..] {
                    if *byte == b'\n' {
                        self.line_bytes = 0;
                    } else {
                        self.line_bytes += 1;
                        if self.line_bytes > MAX_MESSAGE_BYTES {
                            buf.set_filled(before);
                            return Poll::Ready(Err(io::Error::new(
                                io::ErrorKind::InvalidData,
                                "MCP line exceeds 512 KiB",
                            )));
                        }
                    }
                }
                Poll::Ready(Ok(()))
            }
            other => other,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::AsyncReadExt;
    #[tokio::test]
    async fn line_budget_and_newline_reset() {
        let oversized = vec![b'a'; MAX_MESSAGE_BYTES + 1];
        assert!(LimitedReader::new(&oversized[..])
            .read_to_end(&mut Vec::new())
            .await
            .is_err());
        let mut valid = vec![b'a'; MAX_MESSAGE_BYTES];
        valid.push(b'\n');
        valid.extend_from_slice(b"{}\n");
        assert!(LimitedReader::new(&valid[..])
            .read_to_end(&mut Vec::new())
            .await
            .is_ok());
    }
}
