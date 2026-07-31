use std::io::ErrorKind;
use std::thread;

use tulpar_network::UdpTransport;

pub struct NetworkEngine {
    transport: UdpTransport,
}

impl NetworkEngine {
    pub fn new(bind_addr: &str) -> Self {
        let transport = UdpTransport::bind(bind_addr)
            .expect("Failed to bind UDP socket");

        Self { transport }
    }

    pub fn send(&self, bytes: &[u8], target: &str) {
        if let Err(err) = self.transport.send(bytes, target) {
            eprintln!("Send error: {}", err);
        }
    }

    pub fn receive(&self) -> std::io::Result<Vec<u8>> {
        let (packet, _) = self.transport.receive()?;
        Ok(packet)
    }

    pub fn start_receiver<F>(&self, callback: F)
    where
        F: Fn(Vec<u8>) + Send + 'static,
    {
        let socket = self
            .transport
            .clone_socket()
            .expect("Failed to clone UDP socket");

        thread::spawn(move || {
            let transport = UdpTransport::from_socket(socket);

            loop {
                match transport.receive() {
                    Ok((packet, _)) => {
                        callback(packet);
                    }

                    Err(err) => {
                        // Windows UDP ICMP Port Unreachable
                        if err.raw_os_error() == Some(10054) {
                            continue;
                        }

                        if err.kind() == ErrorKind::WouldBlock {
                            continue;
                        }

                        eprintln!("Receiver error: {}", err);
                    }
                }
            }
        });
    }

    pub fn transport(&self) -> &UdpTransport {
        &self.transport
    }
}