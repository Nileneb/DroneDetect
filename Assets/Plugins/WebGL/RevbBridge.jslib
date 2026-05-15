var RevbBridge = {
    $revb_socket: null,
    $revb_subscriptions: {},
    $revb_channel: null,
    $revb_socket_key: null,
    $revb_pending: [],
    $revb_connected: false,

    RevbConnect: function(urlPtr, appKeyPtr, channelNamePtr, tokenPtr) {
        var url = UTF8ToString(urlPtr);
        var appKey = UTF8ToString(appKeyPtr);
        var channelName = UTF8ToString(channelNamePtr);
        var token = UTF8ToString(tokenPtr);

        revb_channel = channelName;
        revb_socket_key = appKey;

        var wsUrl = url + '/app/' + appKey + '?protocol=7&client=js&version=7.0.6&flash=false';
        revb_socket = new WebSocket(wsUrl);

        revb_socket.onopen = function() {
            // Pusher protocol: subscribe after connection_established
        };

        revb_socket.onmessage = function(e) {
            var msg = JSON.parse(e.data);

            if (msg.event === 'pusher:connection_established') {
                var data = JSON.parse(msg.data);
                revb_connected = true;

                // Subscribe to private channel
                var authData = JSON.stringify({
                    socket_id: data.socket_id,
                    channel_name: 'private-' + channelName
                });

                // Use token for auth via API
                var xhr = new XMLHttpRequest();
                xhr.open('POST', '/broadcasting/auth', true);
                xhr.setRequestHeader('Content-Type', 'application/json');
                xhr.setRequestHeader('Authorization', 'Bearer ' + token);
                xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                xhr.onload = function() {
                    if (xhr.status === 200) {
                        var authResp = JSON.parse(xhr.responseText);
                        revb_socket.send(JSON.stringify({
                            event: 'pusher:subscribe',
                            data: {
                                channel: 'private-' + channelName,
                                auth: authResp.auth
                            }
                        }));
                    }
                };
                xhr.send(JSON.stringify({ socket_id: data.socket_id, channel_name: 'private-' + channelName }));

                // Flush pending
                for (var i = 0; i < revb_pending.length; i++) {
                    revb_socket.send(revb_pending[i]);
                }
                revb_pending = [];
            }

            if (msg.event && revb_subscriptions[msg.event]) {
                var cb = revb_subscriptions[msg.event];
                var payload = typeof msg.data === 'string' ? msg.data : JSON.stringify(msg.data);
                dynCall_vi(cb, allocate(intArrayFromString(payload), ALLOC_STACK));
            }
        };

        revb_socket.onerror = function(e) {
            console.error('[RevbBridge] WebSocket error', e);
        };
    },

    RevbSubscribe: function(eventPtr, callbackFnPtr) {
        var eventName = UTF8ToString(eventPtr);
        revb_subscriptions[eventName] = callbackFnPtr;
    },

    RevbTrigger: function(eventPtr, dataPtr) {
        var eventName = UTF8ToString(eventPtr);
        var data = UTF8ToString(dataPtr);
        var msg = JSON.stringify({
            event: 'client-' + eventName,
            channel: 'private-' + revb_channel,
            data: data
        });
        if (revb_connected && revb_socket && revb_socket.readyState === 1) {
            revb_socket.send(msg);
        } else {
            revb_pending.push(msg);
        }
    },

    RevbDisconnect: function() {
        if (revb_socket) {
            revb_socket.close();
            revb_socket = null;
        }
        revb_connected = false;
        revb_subscriptions = {};
        revb_pending = [];
    }
};

autoAddDeps(RevbBridge, '$revb_socket');
autoAddDeps(RevbBridge, '$revb_subscriptions');
autoAddDeps(RevbBridge, '$revb_channel');
autoAddDeps(RevbBridge, '$revb_socket_key');
autoAddDeps(RevbBridge, '$revb_pending');
autoAddDeps(RevbBridge, '$revb_connected');
mergeInto(LibraryManager.library, RevbBridge);
