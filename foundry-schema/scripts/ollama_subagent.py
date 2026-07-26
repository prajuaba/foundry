#!/usr/bin/env python3
import sys
import os
import json
import urllib.request
import argparse

def main():
    parser = argparse.ArgumentParser(description="Ollama sub-agent for automated coding tasks")
    parser.add_argument("--prompt", required=True, help="Instruction or task for the sub-agent")
    parser.add_argument("--inputs", help="Comma-separated paths to input context files")
    parser.add_argument("--output", required=True, help="Path where the generated file should be saved")
    parser.add_argument("--model", default="qwen3-coder:30b", help="Ollama model to use")
    
    args = parser.parse_args()

    # Read context files
    context_str = ""
    if args.inputs:
        for file_path in args.inputs.split(","):
            file_path = file_path.strip()
            if os.path.exists(file_path):
                with open(file_path, "r", encoding="utf-8") as f:
                    context_str += f"\n--- Context File: {file_path} ---\n"
                    context_str += f.read()
                    context_str += "\n--------------------------------\n"
            else:
                print(f"Warning: context file not found: {file_path}", file=sys.stderr)

    user_content = f"{args.prompt}\n\n"
    if context_str:
        user_content += f"Here is the context/existing files you should refer to:\n{context_str}\n"

    user_content += f"Please generate/edit the code and save the result. Your output must strictly contain ONLY the code content (without any conversational remarks or explanations) wrapped in a ``` (triple backticks) block. Do not include any other text outside the block."

    # Construct chat messages
    messages = [
        {
            "role": "system",
            "content": "You are a precise coding sub-agent. Solve the task provided. Return ONLY the code for the requested file in a single markdown code block (using triple backticks), with no other conversational text, notes, or warnings."
        },
        {
            "role": "user",
            "content": user_content
        }
    ]

    print(f"Sending task to local Ollama model '{args.model}'...")
    req_body = {
        "model": args.model,
        "messages": messages,
        "options": {
            "temperature": 0.1
        },
        "stream": False
    }

    ollama_host = os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434")
    if not ollama_host.endswith("/"):
        ollama_host += "/"
    api_url = ollama_host + "api/chat" if "api/chat" not in ollama_host else ollama_host

    print(f"Connecting to Ollama host: {api_url}")
    req_data = json.dumps(req_body).encode("utf-8")
    req = urllib.request.Request(
        api_url,
        data=req_data,
        headers={"Content-Type": "application/json"}
    )

    try:
        with urllib.request.urlopen(req) as response:
            res_body = json.loads(response.read().decode("utf-8"))
            content = res_body["message"]["content"]
            
            # Extract content from markdown block if present
            code_content = content.strip()
            if "```" in code_content:
                # Find start of code block
                lines = code_content.split("\n")
                in_block = False
                block_lines = []
                for line in lines:
                    if line.strip().startswith("```"):
                        if not in_block:
                            in_block = True
                            continue
                        else:
                            break
                    if in_block:
                        block_lines.append(line)
                code_content = "\n".join(block_lines)
            
            # Write to output file
            os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
            with open(args.output, "w", encoding="utf-8") as out_f:
                out_f.write(code_content)
                
            print(f"Successfully generated code and wrote to: {args.output}")
            
    except Exception as e:
        print(f"Error calling Ollama API: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
