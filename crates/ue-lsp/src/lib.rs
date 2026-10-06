//! Language server for Unreal Engine C++ projects.
//!
//! The crate is split so that everything except [`server`] is testable without
//! standing up a server or an editor: coordinate arithmetic in [`text`], the
//! static specifier catalog in [`catalog`], parser access in [`parse`], caret
//! context detection in [`context`], the open-document store in [`docs`],
//! workspace scanning in [`index`], and the request handlers themselves in
//! [`features`]. [`server`] is the thin layer that maps LSP requests onto them.

pub mod catalog;
pub mod context;
mod diagnostics;
pub mod docs;
pub mod extensions;
pub mod features;
pub mod index;
pub mod parse;
pub mod server;
pub mod text;

pub use docs::{Document, Documents};
