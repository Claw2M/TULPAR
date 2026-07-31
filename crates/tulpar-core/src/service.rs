use std::future::Future;
use std::pin::Pin;

pub trait Service: Send {
    fn name(&self) -> &'static str;

    fn start(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>>;

    fn stop(&mut self) -> Pin<Box<dyn Future<Output = ()> + Send>>;
}