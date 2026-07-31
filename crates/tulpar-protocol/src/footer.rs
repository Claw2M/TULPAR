use crc32fast::Hasher;

#[derive(Debug, Clone)]
pub struct Footer {
    pub crc32: u32,
}

impl Footer {
    pub fn new() -> Self {
        Self {
            crc32: 0,
        }
    }

    pub fn calculate(data: &[u8]) -> Self {
        let mut hasher = Hasher::new();

        hasher.update(data);

        Self {
            crc32: hasher.finalize(),
        }
    }

    pub fn verify(&self, data: &[u8]) -> bool {
        let mut hasher = Hasher::new();

        hasher.update(data);

        hasher.finalize() == self.crc32
    }
}