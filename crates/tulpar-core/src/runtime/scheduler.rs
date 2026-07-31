use std::future::Future;

pub struct Scheduler;

impl Scheduler {
    pub fn spawn<F>(future: F)
    where
        F: Future<Output = ()> + Send + 'static,
    {
        tokio::spawn(future);
    }
}