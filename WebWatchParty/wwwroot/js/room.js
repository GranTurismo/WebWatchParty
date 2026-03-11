(() => {
    const root = document.getElementById("roomApp");
    if (!root) {
        return;
    }

    const roomId = root.dataset.roomId ?? "";
    const userName = root.dataset.userName ?? "";
    const sessionStorageKey = `webwatchparty:session:${roomId}:${userName}`;
    const sessionId = sessionStorage.getItem(sessionStorageKey) ?? crypto.randomUUID();
    sessionStorage.setItem(sessionStorageKey, sessionId);

    const dom = {
        alert: document.getElementById("roomAlert"),
        connectionBadge: document.getElementById("connectionBadge"),
        adminBadge: document.getElementById("adminBadge"),
        roomModeLabel: document.getElementById("roomModeLabel"),
        syncStatus: document.getElementById("syncStatus"),
        videoUrl: document.getElementById("videoUrl"),
        changeUrlBtn: document.getElementById("changeUrlBtn"),
        playBtn: document.getElementById("btnPlay"),
        pauseBtn: document.getElementById("btnPause"),
        chatMessages: document.getElementById("chatMessages"),
        chatInput: document.getElementById("chatInput"),
        sendBtn: document.getElementById("sendBtn"),
        participantsList: document.getElementById("participantsList"),
        participantCount: document.getElementById("participantCount"),
        voiceStatusBadge: document.getElementById("voiceStatusBadge"),
        voiceStatusText: document.getElementById("voiceStatusText"),
        voiceJoinBtn: document.getElementById("voiceJoinBtn"),
        voiceMuteBtn: document.getElementById("voiceMuteBtn"),
        voiceLeaveBtn: document.getElementById("voiceLeaveBtn"),
        audioStage: document.getElementById("audioStage")
    };

    const state = {
        isAdmin: false,
        selfConnectionId: "",
        participants: [],
        pendingVideoUrl: "",
        pendingPlayback: null,
        suppressNextSync: false,
        player: null,
        initialized: false,
        voiceActive: false,
        micMuted: false,
        localStream: null,
        peers: new Map()
    };

    const supportsVoice = Boolean(window.RTCPeerConnection && navigator.mediaDevices?.getUserMedia);
    const rtcConfig = {
        iceServers: [{ urls: "stun:stun.l.google.com:19302" }]
    };

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/watchHub")
        .withAutomaticReconnect()
        .build();

    connection.on("InitRoom", (room, adminStatus, connectionId) => {
        state.selfConnectionId = connectionId;
        state.isAdmin = adminStatus;
        hydrateRoom(room);
        setConnectionBadge("Connected", "success");
        hideAlert();

        if (!state.initialized) {
            appendSystemMessage("Connected to watch party.");
            state.initialized = true;
        }
    });

    connection.on("JoinRejected", (message) => {
        handleRoomExit(message || "Unable to join this room.");
    });

    connection.on("ModerationFailed", (message) => {
        showAlert(message || "That moderation action could not be completed.");
    });

    connection.on("UserJoined", (joinedUser) => {
        appendSystemMessage(`${joinedUser} joined the room.`);
    });

    connection.on("UserLeft", (leftUser) => {
        appendSystemMessage(`${leftUser} left the room.`);
    });

    connection.on("UserKicked", (targetUser, adminUser) => {
        appendSystemMessage(`${adminUser} removed ${targetUser} from the room.`);
    });

    connection.on("AdminChanged", (newAdmin) => {
        appendSystemMessage(`${newAdmin} is now the room admin.`);
    });

    connection.on("ParticipantsUpdated", async (participants, adminConnectionId) => {
        const wasAdmin = state.isAdmin;
        state.participants = Array.isArray(participants) ? participants : [];
        state.isAdmin = adminConnectionId === state.selfConnectionId;
        renderParticipants();
        applyAdminState();

        if (!wasAdmin && state.isAdmin && state.initialized) {
            appendSystemMessage("You are now the room admin.");
        }

        await syncVoiceConnections();
    });

    connection.on("ReceiveMessage", (messageUser, message) => {
        appendMessage(messageUser, message);
    });

    connection.on("VideoChanged", (url) => {
        loadVideo(url, true);
        dom.syncStatus.textContent = "Loaded a new video for the room.";
    });

    connection.on("VideoSynced", (action, time) => {
        applyPlaybackState(action, time);
        dom.syncStatus.textContent = `Last sync: ${action} at ${Math.round(time)}s`;
    });

    connection.on("ReceiveVoiceOffer", async (fromConnectionId, fromUserName, offerJson) => {
        if (!state.voiceActive) {
            return;
        }

        try {
            const peer = ensurePeerConnection(fromConnectionId, fromUserName);
            await attachLocalTracks(peer.pc);
            await peer.pc.setRemoteDescription(JSON.parse(offerJson));

            const answer = await peer.pc.createAnswer();
            await peer.pc.setLocalDescription(answer);
            await connection.invoke("SendVoiceAnswer", roomId, fromConnectionId, JSON.stringify(peer.pc.localDescription));
        } catch (error) {
            console.error("Failed to process voice offer", error);
        }
    });

    connection.on("ReceiveVoiceAnswer", async (fromConnectionId, answerJson) => {
        const peer = state.peers.get(fromConnectionId);
        if (!peer) {
            return;
        }

        try {
            await peer.pc.setRemoteDescription(JSON.parse(answerJson));
        } catch (error) {
            console.error("Failed to process voice answer", error);
        }
    });

    connection.on("ReceiveIceCandidate", async (fromConnectionId, candidateJson) => {
        const peer = state.peers.get(fromConnectionId);
        if (!peer) {
            return;
        }

        try {
            await peer.pc.addIceCandidate(JSON.parse(candidateJson));
        } catch (error) {
            console.error("Failed to add ICE candidate", error);
        }
    });

    connection.on("Kicked", (message) => {
        handleRoomExit(message || "You were removed from this room.");
    });

    connection.onreconnecting(() => {
        setConnectionBadge("Reconnecting…", "warning");
    });

    connection.onreconnected(async () => {
        setConnectionBadge("Reconnected", "success");
        resetVoicePeers();
        await joinRoom();
    });

    connection.onclose(() => {
        setConnectionBadge("Disconnected", "danger");
    });

    dom.changeUrlBtn.addEventListener("click", async () => {
        const url = dom.videoUrl.value.trim();
        if (!url) {
            return;
        }

        await invokeHub("ChangeVideo", roomId, url);
    });

    dom.videoUrl.addEventListener("keydown", async (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            dom.changeUrlBtn.click();
        }
    });

    dom.playBtn.addEventListener("click", () => {
        state.player?.playVideo?.();
    });

    dom.pauseBtn.addEventListener("click", () => {
        state.player?.pauseVideo?.();
    });

    dom.sendBtn.addEventListener("click", sendMessage);
    dom.chatInput.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            sendMessage();
        }
    });

    dom.voiceJoinBtn.addEventListener("click", enableVoice);
    dom.voiceMuteBtn.addEventListener("click", toggleMute);
    dom.voiceLeaveBtn.addEventListener("click", () => leaveVoice({ notifyServer: true }));

    window.onYouTubeIframeAPIReady = () => {
        state.player = new YT.Player("player", {
            height: "100%",
            width: "100%",
            videoId: "",
            playerVars: {
                rel: 0,
                modestbranding: 1
            },
            events: {
                onReady: onPlayerReady,
                onStateChange: onPlayerStateChange
            }
        });
    };

    window.addEventListener("beforeunload", () => {
        leaveVoice({ notifyServer: false, silent: true });
        connection.stop();
    });

    if (!supportsVoice) {
        dom.voiceJoinBtn.disabled = true;
        dom.voiceStatusBadge.textContent = "Unavailable";
        dom.voiceStatusText.textContent = "This browser does not support voice chat for this room.";
    }

    applyAdminState();
    updateVoiceUi();
    start();

    async function start() {
        try {
            setConnectionBadge("Connecting…", "muted");
            await connection.start();
            await joinRoom();
        } catch (error) {
            console.error(error);
            setConnectionBadge("Retrying…", "warning");
            window.setTimeout(start, 3000);
        }
    }

    async function joinRoom() {
        await connection.invoke("JoinRoom", roomId, userName, sessionId);
    }

    function hydrateRoom(room) {
        clearMessages();
        for (const message of room?.messages ?? []) {
            appendMessage(message.user, message.message);
        }

        state.pendingVideoUrl = room?.currentVideoUrl ?? "";
        state.pendingPlayback = room?.currentVideoUrl
            ? { action: room.action, time: room.currentTime }
            : null;

        if (state.pendingVideoUrl) {
            loadVideo(state.pendingVideoUrl, true);
        }

        state.participants = Array.isArray(room?.participants) ? room.participants : [];
        renderParticipants();
        applyAdminState();
    }

    function clearMessages() {
        dom.chatMessages.innerHTML = "";
    }

    async function sendMessage() {
        const message = dom.chatInput.value.trim();
        if (!message) {
            return;
        }

        const sent = await invokeHub("SendMessage", roomId, message);
        if (sent) {
            dom.chatInput.value = "";
        }
    }

    function appendMessage(author, message) {
        const row = document.createElement("div");
        row.className = "chat-message";

        if (author === "system") {
            row.classList.add("chat-message-system");
            const systemText = document.createElement("span");
            systemText.textContent = message;
            row.appendChild(systemText);
        } else {
            const authorSpan = document.createElement("span");
            authorSpan.className = "chat-message-author";
            authorSpan.textContent = `${author}:`;

            const messageSpan = document.createElement("span");
            messageSpan.className = "chat-message-body";
            messageSpan.textContent = message;

            row.append(authorSpan, messageSpan);
        }

        dom.chatMessages.appendChild(row);
        dom.chatMessages.scrollTop = dom.chatMessages.scrollHeight;
    }

    function appendSystemMessage(message) {
        appendMessage("system", message);
    }

    function renderParticipants() {
        const participants = [...state.participants];
        dom.participantCount.textContent = `${participants.length} online`;
        dom.participantsList.innerHTML = "";

        if (!participants.length) {
            const emptyState = document.createElement("div");
            emptyState.className = "participant-empty";
            emptyState.textContent = "No one is in the room yet.";
            dom.participantsList.appendChild(emptyState);
            return;
        }

        for (const participant of participants) {
            const row = document.createElement("div");
            row.className = "participant-row";

            const meta = document.createElement("div");
            meta.className = "participant-meta";

            const name = document.createElement("div");
            name.className = "participant-name";
            name.textContent = participant.userName;

            const badges = document.createElement("div");
            badges.className = "participant-badges";

            if (participant.connectionId === state.selfConnectionId) {
                badges.appendChild(buildBadge("You", "participant-badge-self"));
            }

            if (participant.isAdmin) {
                badges.appendChild(buildBadge("Admin", "participant-badge-admin"));
            }

            if (participant.voiceEnabled) {
                badges.appendChild(buildBadge(participant.isMuted ? "Muted" : "Voice live", participant.isMuted ? "participant-badge-muted" : "participant-badge-voice"));
            }

            meta.append(name, badges);
            row.appendChild(meta);

            if (state.isAdmin && participant.connectionId !== state.selfConnectionId) {
                const kickButton = document.createElement("button");
                kickButton.className = "btn btn-sm btn-outline-danger";
                kickButton.type = "button";
                kickButton.textContent = "Kick";
                kickButton.addEventListener("click", async () => {
                    const confirmed = window.confirm(`Kick ${participant.userName} from the room?`);
                    if (!confirmed) {
                        return;
                    }

                    await invokeHub("KickUser", roomId, participant.connectionId);
                });
                row.appendChild(kickButton);
            }

            dom.participantsList.appendChild(row);
        }
    }

    function buildBadge(text, className = "") {
        const badge = document.createElement("span");
        badge.className = `participant-badge ${className}`.trim();
        badge.textContent = text;
        return badge;
    }

    function applyAdminState() {
        dom.adminBadge.classList.toggle("d-none", !state.isAdmin);
        dom.roomModeLabel.textContent = state.isAdmin ? "Admin mode — you control playback" : "Viewer mode — playback follows the admin";
        dom.videoUrl.disabled = !state.isAdmin;
        dom.changeUrlBtn.disabled = !state.isAdmin;
        dom.playBtn.disabled = !state.isAdmin;
        dom.pauseBtn.disabled = !state.isAdmin;
    }

    function loadVideo(url, suppressSync) {
        const videoId = extractYouTubeId(url);
        if (!videoId) {
            showAlert("Please paste a valid YouTube URL.");
            return;
        }

        hideAlert();
        dom.videoUrl.value = url;
        state.pendingVideoUrl = url;

        if (suppressSync) {
            state.suppressNextSync = true;
        }

        if (state.player?.loadVideoById) {
            state.player.loadVideoById(videoId);
        }
    }

    function onPlayerReady() {
        if (state.pendingVideoUrl) {
            loadVideo(state.pendingVideoUrl, true);
        }

        if (state.pendingPlayback) {
            window.setTimeout(() => {
                applyPlaybackState(state.pendingPlayback.action, state.pendingPlayback.time);
                state.pendingPlayback = null;
            }, 500);
        }
    }

    function onPlayerStateChange(event) {
        if (!state.isAdmin || state.suppressNextSync) {
            state.suppressNextSync = false;
            return;
        }

        if (connection.state !== signalR.HubConnectionState.Connected || !state.player?.getCurrentTime) {
            return;
        }

        if (event.data === YT.PlayerState.PLAYING) {
            invokeHub("SyncVideo", roomId, "play", state.player.getCurrentTime());
        }

        if (event.data === YT.PlayerState.PAUSED) {
            invokeHub("SyncVideo", roomId, "pause", state.player.getCurrentTime());
        }
    }

    function applyPlaybackState(action, time) {
        if (!state.player?.seekTo) {
            return;
        }

        state.suppressNextSync = true;
        state.player.seekTo(time, true);

        if (action === "play") {
            state.player.playVideo();
            return;
        }

        state.player.pauseVideo();
    }

    function extractYouTubeId(url) {
        try {
            const parsedUrl = new URL(url);
            if (parsedUrl.hostname.includes("youtu.be")) {
                return parsedUrl.pathname.replace("/", "").slice(0, 11);
            }

            if (parsedUrl.searchParams.has("v")) {
                return parsedUrl.searchParams.get("v");
            }

            const segments = parsedUrl.pathname.split("/").filter(Boolean);
            const embedIndex = segments.findIndex((segment) => segment === "embed" || segment === "shorts");
            if (embedIndex >= 0 && segments[embedIndex + 1]) {
                return segments[embedIndex + 1];
            }
        } catch {
            return null;
        }

        return null;
    }

    async function enableVoice() {
        if (!supportsVoice) {
            return;
        }

        if (connection.state !== signalR.HubConnectionState.Connected || !state.selfConnectionId) {
            showAlert("The room is still connecting. Try voice again in a moment.");
            return;
        }

        if (state.voiceActive) {
            return;
        }

        try {
            state.localStream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                },
                video: false
            });

            state.voiceActive = true;
            state.micMuted = false;
            applyLocalTrackState();
            updateVoiceUi();
            hideAlert();

            await invokeHub("UpdateVoiceState", roomId, true, false);
            await syncVoiceConnections();
        } catch (error) {
            console.error("Unable to enable voice", error);
            showAlert("Microphone access was blocked. Allow microphone access to use voice chat.");
            dom.voiceStatusBadge.textContent = "Blocked";
            dom.voiceStatusText.textContent = "Microphone access is required to join voice chat.";
        }
    }

    async function toggleMute() {
        if (!state.voiceActive || !state.localStream) {
            return;
        }

        state.micMuted = !state.micMuted;
        applyLocalTrackState();
        updateVoiceUi();
        await invokeHub("UpdateVoiceState", roomId, true, state.micMuted);
    }

    async function leaveVoice(options = { notifyServer: true, silent: false }) {
        const wasActive = state.voiceActive;

        resetVoicePeers();

        if (state.localStream) {
            for (const track of state.localStream.getTracks()) {
                track.stop();
            }
        }

        state.localStream = null;
        state.voiceActive = false;
        state.micMuted = false;
        updateVoiceUi();

        if (options.notifyServer && wasActive && connection.state === signalR.HubConnectionState.Connected) {
            await invokeHub("UpdateVoiceState", roomId, false, false);
        }

        if (!options.silent && wasActive) {
            dom.voiceStatusText.textContent = "Voice chat disabled for your session.";
        }
    }

    async function syncVoiceConnections() {
        if (!state.voiceActive || connection.state !== signalR.HubConnectionState.Connected) {
            return;
        }

        const activeVoiceParticipants = new Set();

        for (const participant of state.participants) {
            if (participant.connectionId === state.selfConnectionId) {
                continue;
            }

            if (!participant.voiceEnabled) {
                cleanupPeer(participant.connectionId);
                continue;
            }

            activeVoiceParticipants.add(participant.connectionId);

            if (!state.peers.has(participant.connectionId) && shouldInitiateVoiceWith(participant.connectionId)) {
                await createAndSendOffer(participant.connectionId, participant.userName);
            }
        }

        for (const connectionId of [...state.peers.keys()]) {
            if (!activeVoiceParticipants.has(connectionId)) {
                cleanupPeer(connectionId);
            }
        }
    }

    function shouldInitiateVoiceWith(remoteConnectionId) {
        return state.selfConnectionId.localeCompare(remoteConnectionId) > 0;
    }

    async function createAndSendOffer(remoteConnectionId, remoteUserName) {
        try {
            const peer = ensurePeerConnection(remoteConnectionId, remoteUserName);
            await attachLocalTracks(peer.pc);

            const offer = await peer.pc.createOffer();
            await peer.pc.setLocalDescription(offer);
            await connection.invoke("SendVoiceOffer", roomId, remoteConnectionId, JSON.stringify(peer.pc.localDescription));
        } catch (error) {
            console.error("Failed to create voice offer", error);
            cleanupPeer(remoteConnectionId);
        }
    }

    function ensurePeerConnection(remoteConnectionId, remoteUserName) {
        const existing = state.peers.get(remoteConnectionId);
        if (existing) {
            existing.userName = remoteUserName;
            return existing;
        }

        const pc = new RTCPeerConnection(rtcConfig);
        const audioTile = document.createElement("div");
        audioTile.className = "audio-tile";

        const label = document.createElement("span");
        label.className = "audio-tile-label";
        label.textContent = `Listening to ${remoteUserName}`;

        const audio = document.createElement("audio");
        audio.autoplay = true;
        audio.playsInline = true;

        audioTile.append(label, audio);
        dom.audioStage.appendChild(audioTile);

        const peer = {
            pc,
            audio,
            audioTile,
            userName: remoteUserName
        };

        pc.ontrack = (event) => {
            const [remoteStream] = event.streams;
            if (remoteStream) {
                audio.srcObject = remoteStream;
            }
        };

        pc.onicecandidate = (event) => {
            if (event.candidate && connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("SendIceCandidate", roomId, remoteConnectionId, JSON.stringify(event.candidate));
            }
        };

        pc.onconnectionstatechange = () => {
            if (["failed", "closed", "disconnected"].includes(pc.connectionState)) {
                cleanupPeer(remoteConnectionId);
            }
        };

        state.peers.set(remoteConnectionId, peer);
        return peer;
    }

    async function attachLocalTracks(pc) {
        if (!state.localStream) {
            return;
        }

        const existingTrackIds = new Set(pc.getSenders().map((sender) => sender.track?.id).filter(Boolean));
        for (const track of state.localStream.getTracks()) {
            if (!existingTrackIds.has(track.id)) {
                pc.addTrack(track, state.localStream);
            }
        }
    }

    function cleanupPeer(connectionId) {
        const peer = state.peers.get(connectionId);
        if (!peer) {
            return;
        }

        peer.audio.srcObject = null;
        peer.audioTile.remove();
        peer.pc.close();
        state.peers.delete(connectionId);
    }

    function resetVoicePeers() {
        for (const connectionId of [...state.peers.keys()]) {
            cleanupPeer(connectionId);
        }
    }

    function applyLocalTrackState() {
        if (!state.localStream) {
            return;
        }

        for (const track of state.localStream.getAudioTracks()) {
            track.enabled = !state.micMuted;
        }
    }

    function updateVoiceUi() {
        if (!supportsVoice) {
            return;
        }

        dom.voiceJoinBtn.disabled = state.voiceActive;
        dom.voiceMuteBtn.disabled = !state.voiceActive;
        dom.voiceLeaveBtn.disabled = !state.voiceActive;
        dom.voiceMuteBtn.textContent = state.micMuted ? "Unmute mic" : "Mute mic";

        if (!state.voiceActive) {
            dom.voiceStatusBadge.textContent = "Voice off";
            dom.voiceStatusBadge.className = "status-chip status-chip-muted";
            dom.voiceStatusText.textContent = "Enable your microphone to talk with everyone in the room.";
            return;
        }

        dom.voiceStatusBadge.textContent = state.micMuted ? "Muted" : "Voice live";
        dom.voiceStatusBadge.className = state.micMuted ? "status-chip status-chip-warning" : "status-chip status-chip-success";
        dom.voiceStatusText.textContent = state.micMuted
            ? "Your microphone is muted, but you are still connected to voice chat."
            : "You are connected to room voice chat.";
    }

    function setConnectionBadge(text, variant) {
        dom.connectionBadge.textContent = text;
        dom.connectionBadge.className = `status-chip ${connectionBadgeClass(variant)}`;
    }

    function connectionBadgeClass(variant) {
        switch (variant) {
            case "success":
                return "status-chip-success";
            case "warning":
                return "status-chip-warning";
            case "danger":
                return "status-chip-danger";
            default:
                return "status-chip-muted";
        }
    }

    function showAlert(message) {
        dom.alert.textContent = message;
        dom.alert.classList.remove("d-none");
    }

    function hideAlert() {
        dom.alert.textContent = "";
        dom.alert.classList.add("d-none");
    }

    async function handleRoomExit(message) {
        showAlert(message);
        appendSystemMessage(message);
        await leaveVoice({ notifyServer: false, silent: true });
        window.setTimeout(() => {
            window.location.assign(`/?error=${encodeURIComponent(message)}`);
        }, 1200);
    }

    async function invokeHub(methodName, ...args) {
        if (connection.state !== signalR.HubConnectionState.Connected) {
            showAlert("The connection to the room is not ready yet.");
            return false;
        }

        try {
            await connection.invoke(methodName, ...args);
            return true;
        } catch (error) {
            console.error(`Hub call failed: ${methodName}`, error);
            showAlert("Something went wrong while talking to the room. Please try again.");
            return false;
        }
    }
})();

