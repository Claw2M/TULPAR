mod boot;
mod config;
mod logger;
mod event;

use boot::Boot;

fn main() {
    println!("=====================================");
    println!("       TULPAR Core v0.1.0-alpha");
    println!("=====================================");
    println!();

    Boot::start();
}