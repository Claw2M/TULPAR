use std::net::{SocketAddr, UdpSocket};

pub struct UdpTransport {
    socket: UdpSocket,
}

impl UdpTransport {
    pub fn bind(address: &str) -> std::io::Result<Self> {
        let socket = UdpSocket::bind(address)?;

        Ok(Self { socket })
    }

    /// Mevcut bir UdpSocket nesnesini UdpTransport içine sarmalamak için
    pub fn from_socket(socket: UdpSocket) -> Self {
        Self { socket }
    }

    pub fn local_addr(&self) -> std::io::Result<SocketAddr> {
        self.socket.local_addr()
    }

    pub fn send(
        &self,
        bytes: &[u8],
        destination: &str,
    ) -> std::io::Result<usize> {
        self.socket.send_to(bytes, destination)
    }

    pub fn receive(
        &self,
    ) -> std::io::Result<(Vec<u8>, SocketAddr)> {
        let mut buffer = [0u8; 2048];

        let (size, addr) = self.socket.recv_from(&mut buffer)?;

        Ok((buffer[..size].to_vec(), addr))
    }

    /// NetworkEngine için socket erişimi
    pub fn socket(&self) -> &UdpSocket {
        &self.socket
    }

    /// Başka thread'e göndermek için clone
    pub fn clone_socket(&self) -> std::io::Result<UdpSocket> {
        self.socket.try_clone()
    }
}