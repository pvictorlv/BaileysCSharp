# BaileysCSharp

A .NET implementation of the WhatsApp Web API, providing a comprehensive C# library for building WhatsApp bots and automation tools.

## Overview

BaileysCSharp is a feature-rich C# library that enables developers to interact with WhatsApp Web through a programmatic interface. It provides full support for messaging, media handling, group management, and real-time event handling.

## Features

- **Full WhatsApp Web API Implementation**: Complete coverage of WhatsApp Web functionality
- **Real-time Messaging**: Send and receive text messages, media files, and documents
- **Media Support**: Handle images, videos, audio files, documents, and stickers
- **Group Management**: Create groups, manage participants, and handle group metadata
- **Event-Driven Architecture**: React to incoming messages, connection events, and state changes
- **Session Management**: Persistent authentication with QR code login
- **Signal Protocol**: End-to-end encryption support using the Signal protocol
- **Multi-Device Support**: Compatible with WhatsApp's multi-device architecture
- **Newsletter Support**: Handle WhatsApp newsletter functionality
- **Flexible Storage**: Multiple storage backends including memory and file-based stores

## Architecture

The library is organized into several core components:

- **Core**: Main library with socket implementation, event handling, and core functionality
- **Signal**: Signal protocol implementation for encryption/decryption
- **Models**: Data models for messages, contacts, groups, and other WhatsApp entities
- **Events**: Event system for handling real-time updates
- **Storage**: Persistent storage solutions for session data and message history
- **Console Example**: Sample application demonstrating library usage

## Quick Start

### Installation

Add the BaileysCSharp library to your project:

```xml
<ProjectReference Include="path\to\BaileysCSharp\BaileysCSharp.csproj" />
```

### Basic Usage

```csharp
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Sockets;

// Create socket configuration
var config = new SocketConfig
{
    AuthState = authState, // Your authentication state
    Logger = logger,       // Your logger implementation
    // Additional configuration options
};

// Initialize WhatsApp socket
var socket = new WASocket(config);

// Handle incoming messages
socket.EV.On<MessagesEventStore>(EmitType.MessagesUpsert, async (messages) =>
{
    foreach (var message in messages.Messages)
    {
        Console.WriteLine($"Received: {message.Key.RemoteJid} - {message.Message?.Conversation}");
    }
});

// Send a text message
await socket.SendMessage(jid, new TextMessageContent
{
    Text = "Hello from BaileysCSharp!"
});
```

### Authentication

BaileysCSharp supports QR code authentication for linking with WhatsApp:

```csharp
// Handle QR code generation for authentication
socket.EV.On<ConnectionEventStore>(EmitType.ConnectionUpdate, async (update) =>
{
    if (update.QR != null)
    {
        // Display QR code for scanning
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(update.QR, QRCodeGenerator.ECCLevel.Q);
        // Display the QR code to user
    }
});
```

## Project Structure

```
BaileysCSharp/
├── BaileysCSharp/              # Main library
│   ├── Core/
│   │   ├── Events/             # Event handling system
│   │   ├── Helper/             # Utility classes
│   │   ├── Models/             # Data models
│   │   ├── Signal/             # Signal protocol implementation
│   │   ├── Sockets/            # WebSocket and connection handling
│   │   └── Utils/              # General utilities
│   └── Proto/                  # Protocol buffer definitions
├── WhatsSocketConsole/         # Example console application
├── BaileysCSharp.Tests/        # Unit tests
└── README.md                   # This file
```

## Key Components

### WASocket
The main interface for interacting with WhatsApp Web. Provides methods for:
- Sending messages and media
- Managing connections
- Handling authentication
- Group operations

### Event System
Real-time event handling for:
- Message updates (new, edited, deleted)
- Connection state changes
- Contact and group updates
- Presence updates

### Storage System
Flexible storage backends:
- **MemoryStore**: In-memory storage for temporary sessions
- **FileKeyStore**: File-based persistent storage
- Custom storage implementations

## Dependencies

- **.NET 8.0**: Target framework
- **Google.Protobuf**: Protocol buffer support
- **Portable.BouncyCastle**: Cryptographic operations
- **FFMpegCore**: Media processing
- **SkiaSharp**: Image processing
- **LiteDB**: Lightweight database for storage
- **QRCoder**: QR code generation (console example)

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

## License

This project is open source. Please check the license file for details.

## Disclaimer

This library is not officially affiliated with WhatsApp or Meta. Use responsibly and in accordance with WhatsApp's Terms of Service.

## Support

For issues, questions, or contributions, please use the GitHub issue tracker.
