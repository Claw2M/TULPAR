use tulpar_network::UdpTransport;

fn main() {
    println!("=====================================");
    println!("      TULPAR UDP Listener");
    println!("=====================================");

    let transport = UdpTransport::bind("127.0.0.1:14550")
        .expect("Failed to bind socket");

    println!("Listening on 127.0.0.1:14550...");

    loop {
        let (packet, sender) = transport.receive().unwrap();

        println!("-------------------------------------");
        println!("Packet received from {}", sender);
        println!("{:02X?}", packet);
    }
}