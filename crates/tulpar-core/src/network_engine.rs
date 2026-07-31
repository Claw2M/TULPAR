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
        self.transport
            .send(bytes, target)
            .expect("Failed to send packet");
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
            .socket()
            .try_clone()
            .expect("Failed to clone UDP socket");

        let transport = UdpTransport::from_socket(socket);

        thread::spawn(move || {
            loop {
                match transport.receive() {
                    Ok((packet, _)) => callback(packet),

                    Err(err) => {
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