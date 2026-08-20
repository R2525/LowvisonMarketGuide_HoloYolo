import socket
import json
import time

HOST = '0.0.0.0'  # Listen on all network interfaces
PORT = 6000       # Must match Unity's Flutter Server Port

def start_server():
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.bind((HOST, PORT))
    server_socket.listen(1)
    
    print(f"Server listening on {HOST}:{PORT}...")
    
    while True:
        try:
            print("Waiting for Unity connection...")
            client_socket, addr = server_socket.accept()
            print(f"Connected by {addr}")
            
            # Buffer for handling potentially fragmented or bundled data
            buffer = ""
            
            while True:
                data = client_socket.recv(1024)
                if not data:
                    print("Connection closed by Unity.")
                    break
                
                # Decode and accumulate
                text_chunk = data.decode('utf-8')
                buffer += text_chunk
                
                # Process complete commands (newline terminated)
                while '\n' in buffer:
                    message, buffer = buffer.split('\n', 1)
                    if message.strip():
                        process_message(message)
                        
            client_socket.close()
            
        except Exception as e:
            print(f"Error: {e}")
            time.sleep(1)

def process_message(json_str):
    try:
        data = json.loads(json_str)
        msg_type = data.get("type", "unknown")
        
        print(f"\n[Received] Type: {msg_type}")
        
        if msg_type == "guidance":
            # Check for directions
            directions = []
            if "right" in data: directions.append(f"Right({data['right']})")
            if "left" in data: directions.append(f"Left({data['left']})")
            if "top" in data: directions.append(f"Up({data['top']})")
            if "bottom" in data: directions.append(f"Down({data['bottom']})")
            if "depth" in data: directions.append(f"Depth({data['depth']})")
            
            print(f" -> Guidance: {', '.join(directions)}")
            
        elif msg_type == "found":
            print(f" -> EVENT: Target Found! ({data.get('message', '')})")
            
        elif msg_type == "grabbed":
            print(f" -> EVENT: Object Grabbed! ({data.get('message', '')})")
            
        else:
            print(f" -> Raw: {json_str}")
            
    except json.JSONDecodeError:
        print(f"(!) Invalid JSON: {json_str}")

if __name__ == "__main__":
    start_server()
