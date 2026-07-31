use tulpar_network::UdpTransport;
use tulpar_protocol::{Packet, PacketType};

fn main() {
    println!("===============================");
    println!("      TULPAR Listener");
    println!("===============================");

    let transport = UdpTransport::bind("127.0.0.1:14550")
        .expect("Failed to bind listener");

    println!("[LISTENER] Listening on 127.0.0.1:14550");

    loop {
        match transport.receive() {
            Ok((bytes, addr)) => {
                println!(
                    "\n[RECV] {} bytes from {}",
                    bytes.len(),
                    addr
                );

                let packet = match Packet::from_bytes(&bytes) {
                    Some(packet) => packet,
                    None => {
                        println!("[ERROR] Invalid packet.");
                        continue;
                    }
                };

                match packet.header.packet_type {
                    PacketType::Heartbeat => {
                        println!("[HEARTBEAT]");
                        println!("Sequence : {}", packet.header.sequence);
                    }

                    PacketType::Telemetry => {
                        println!("[TELEMETRY]");
                        println!("Payload Size : {}", packet.payload.len());
                        println!("{:02X?}", packet.payload);
                    }

                    PacketType::Command => {
                        println!("[COMMAND]");
                    }
                }
            }

            Err(err) => {
                println!("Receive Error: {}", err);
            }
        }
    }
}