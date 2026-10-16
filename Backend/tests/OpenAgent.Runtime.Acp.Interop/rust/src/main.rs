use agent_client_protocol::schema::v1::*;
use agent_client_protocol::{AcpAgent, Agent, Client, ConnectionTo, Stdio};
use serde_json::json;
use std::{
    str::FromStr,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};
use tokio::sync::Notify;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();
    let cancelled = Arc::new(Notify::new());
    if args[1] == "agent" {
        let prompt_cancel = cancelled.clone();
        Agent.builder()
            .on_receive_request(async |_: InitializeRequest, r, _| r.respond(serde_json::from_value::<InitializeResponse>(json!({"protocolVersion":1,"agentCapabilities":{}})).unwrap()), agent_client_protocol::on_receive_request!())
            .on_receive_request(async |_: NewSessionRequest, r, _| r.respond(serde_json::from_value::<NewSessionResponse>(json!({"sessionId":"rust-session"})).unwrap()), agent_client_protocol::on_receive_request!())
            .on_receive_request(async move |p: PromptRequest, r: agent_client_protocol::Responder<PromptResponse>, conn: ConnectionTo<Client>| {
                let cancel = prompt_cancel.clone();
                let worker = conn.clone();
                conn.spawn(async move {
                let slow = serde_json::to_value(&p).unwrap()["prompt"][0]["text"] == "slow";
                worker.send_notification(serde_json::from_value::<SessionNotification>(json!({"sessionId":p.session_id,"update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":if slow {"started"} else {"你好🥐"}}}})).unwrap())?;
                if slow { cancel.notified().await; }
                r.respond(serde_json::from_value::<PromptResponse>(json!({"stopReason":if slow {"cancelled"} else {"end_turn"}})).unwrap())
                })?;
                Ok(())
            }, agent_client_protocol::on_receive_request!())
            .on_receive_notification(async move |_: CancelNotification, _| { cancelled.notify_one(); Ok(()) }, agent_client_protocol::on_receive_notification!())
            .connect_to(Stdio::new()).await?;
    } else {
        let target = AcpAgent::from_str(&args[2])?;
        let text = Arc::new(Mutex::new(String::new()));
        let notifications = text.clone();
        let permissions = Arc::new(AtomicUsize::new(0));
        let received = permissions.clone();
        Client.builder()
            .on_receive_request(async move |_: RequestPermissionRequest, r, _| { received.fetch_add(1, Ordering::SeqCst); r.respond(serde_json::from_value::<RequestPermissionResponse>(json!({"outcome":{"outcome":"cancelled"}})).unwrap()) }, agent_client_protocol::on_receive_request!())
            .on_receive_notification(async move |n: SessionNotification, _| { let wire = serde_json::to_value(&n)?; if wire["update"]["sessionUpdate"] == "agent_message_chunk" { notifications.lock().unwrap().push_str(wire["update"]["content"]["text"].as_str().unwrap()); } Ok(()) }, agent_client_protocol::on_receive_notification!())
            .connect_with(target, |conn: ConnectionTo<Agent>| async move {
                conn.send_request(InitializeRequest::new(agent_client_protocol::schema::ProtocolVersion::V1)).block_task().await?;
                let session = conn.send_request(NewSessionRequest::new(std::env::current_dir().unwrap())).block_task().await?;
                for text in ["你好🥐", "permission", "slow"] {
                    let prompt: PromptRequest = serde_json::from_value(json!({"sessionId":session.session_id,"prompt":[{"type":"text","text":text}]})).unwrap();
                    let result = conn.send_request(prompt).block_task();
                    if text == "slow" {
                        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
                        conn.send_notification(serde_json::from_value::<CancelNotification>(json!({"sessionId":session.session_id})).unwrap())?;
                    }
                    let response = result.await?;
                    assert_eq!(serde_json::to_value(&response).unwrap()["stopReason"], if text == "slow" {"cancelled"} else {"end_turn"});
                }
                assert!(text.lock().unwrap().contains("Echo: 你好🥐"));
                assert!(text.lock().unwrap().contains("cancelled"));
                assert_eq!(permissions.load(Ordering::SeqCst), 1);
                Ok(())
            }).await?;
        println!(
            "PASS Rust client -> C# agent: prompt, reverse permission, confirmed cancellation"
        );
    }
    Ok(())
}
