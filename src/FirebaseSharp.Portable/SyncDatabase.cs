﻿using System;
using System.Collections.Generic;
using System.Linq;
using FirebaseSharp.Portable.Interfaces;
using FirebaseSharp.Portable.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FirebaseSharp.Portable
{
    /// <summary>
    /// The SyncDatabase is a single JSON object that represents the state of the 
    /// data as we know it.  As changes come in the object is updated 
    /// and the update communicated via an event.
    /// 
    /// Set changes the path exactly as directed
    /// Update merges the changes (over-writing specifically named children, leaving the rest untouched)
    /// Delete by Set or Update with a NULL data member.
    /// 
    /// Thoughts on the future...
    /// 
    /// What's the deal with syncing the tree.  Should we just apply deltas as they come in,
    /// in the order they arrive, or should we look at the tree as a whole and perform diffing
    /// against another tree?  This would require things like dirty flags, etc - but would make synching
    /// from a saved state possible (but is it a good idea?)
    /// Would JSON Patch make sense?  It's basically what the REST APIs are sending anyway.
    /// How should conflicts be handled?
    /// </summary>
    class SyncDatabase : IDisposable
    {
        private JToken _root;
        private bool _initialReceive;
        private readonly object _lock = new object();
        private readonly IFirebaseNetworkConnection _connection;
        private readonly FirebasePushIdGenerator _idGenerator = new FirebasePushIdGenerator();
        private readonly Queue<Subscription> _initialSubscriptions = new Queue<Subscription>();
        private readonly FirebaseApp _app;

        public SyncDatabase(FirebaseApp app, IFirebaseNetworkConnection connection)
        {
            _app = app;
            _root = new JObject();

            _connection = connection;
            _connection.Received += ConnectionOnReceived;
        }

        public EventHandler<JsonCacheUpdateEventArgs> Changed;

        public DataSnapshot SnapFor(FirebasePath path)
        {
            lock (_lock)
            {
                JToken token;

                if (TryGetChild(path, out token))
                {
                    return new DataSnapshot(_app, path, token);
                }

                return new DataSnapshot(_app, null, null);
            }
        }

        private void ConnectionOnReceived(object sender, FirebaseEventReceivedEventArgs e)
        {
            lock (_lock)
            {
                switch (e.Message.Behavior)
                {
                    case WriteBehavior.Replace:
                        Set(e.Message);
                        DrainInitialQueue();
                        break;
                    case WriteBehavior.Merge:
                        Update(e.Message);
                        break;
                }
            }
        }

        private void DrainInitialQueue()
        {
            _initialReceive = true;
            while (_initialSubscriptions.Any())
            {
                var sub = _initialSubscriptions.Dequeue();
                sub.Process(this);
            }
        }

        private JToken CreateToken(string value)
        {
            return value.Trim().StartsWith("{")
                ? JToken.Parse(value)
                : new JValue(value);
        }

        public void Set(FirebasePath path, string data, FirebaseStatusCallback callback)
        {
            Set(path, data, null, callback);
        }

        private void Set(FirebasePath path, string data, FirebasePriority priority, FirebaseStatusCallback callback)
        {
            var message = new FirebaseMessage(WriteBehavior.Replace, path, data, priority, callback);

            lock (_lock)
            {
                Set(message);
                QueueUpdate(message);
            }
        }

        private void Set(FirebaseMessage message)
        {
            if (message.Value == null)
            {
                Delete(message.Path);
            }
            else
            {
                JToken newData = CreateToken(message.Value);
                if (message.Priority != null)
                {
                    newData[".priority"] = new JValue(message.Priority.JsonValue);
                }

                lock (_lock)
                {
                    JToken found;
                    if (TryGetChild(message.Path, out found))
                    {
                        Replace(found, newData);
                    }
                    else
                    {
                        InsertAt(message.Path, newData);
                    }
                }
            }

            OnChanged(message);
        }

        private void QueueUpdate(FirebaseMessage firebaseMessage)
        {
            _connection.Send(firebaseMessage);
        }

        private void Replace(JToken found, JToken newData)
        {
            if (!UpdateValues(found, newData))
            {
                if (found.Parent != null)
                {
                    found.Replace(newData);
                }
                else
                {
                    _root = newData;
                }
            }
        }

        public string Push(FirebasePath path, string data, FirebaseStatusCallback callback)
        {
            string childPath = _idGenerator.Next();

            if (data != null)
            {
                lock (_lock)
                {
                    Set(path.Child(childPath), data, callback);
                }
            }

            return childPath;
        }

        public void Update(FirebasePath path, string data, FirebaseStatusCallback callback)
        {
            var message = new FirebaseMessage(WriteBehavior.Merge, path, data, callback);

            lock (_lock)
            {
                Update(message);
                QueueUpdate(message);
            }
        }

        private void Update(FirebaseMessage message)
        {
            if (message.Value == null)
            {
                Delete(message.Path);
            }
            else
            {
                JToken newData = CreateToken(message.Value);

                lock (_lock)
                {
                    JToken found;
                    if (TryGetChild(message.Path, out found))
                    {
                        Merge(found, newData);
                    }
                    else
                    {
                        InsertAt(message.Path, newData);
                    }
                }
            }

            OnChanged(message);
        }

        private void OnChanged(FirebaseMessage message)
        {
            var callback = Changed;
            if (callback != null)
            {
                callback(this, new JsonCacheUpdateEventArgs(message.Path));
            }
        }
        private void Delete(FirebasePath path)
        {
            lock (_lock)
            {
                JToken node;
                if (TryGetChild(path, out node))
                {
                    if (node.Parent != null)
                    {
                        node.Parent.Remove();
                    }
                    else
                    {
                        _root = new JObject();
                    }
                }
            }
        }

        internal string Dump()
        {
            lock (_lock)
            {
                return _root.ToString(Formatting.None);
            }
        }

        private bool UpdateValues(JToken oldToken, JToken newToken)
        {
            JValue oldVal = oldToken as JValue;
            JValue newVal = newToken as JValue;

            if (oldVal != null && newVal != null)
            {
                oldVal.Value = newVal.Value;
                return true;
            }

            return false;
        }
        private void InsertAt(FirebasePath path, JToken newData)
        {
            string[] segments = path.Segments.ToArray();

            if (segments.Length > 0)
            {
                var node = _root;

                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string segment = segments[i];
                    var child = node[segment];
                    if (child == null)
                    {
                        node[segment] = new JObject();
                        node = node[segment];
                    }
                    else
                    {
                        node = child;
                    }
                }

                node[segments[segments.Length - 1]] = newData;
            }
            else
            {
                _root = newData;
            }
        }

        private void Merge(JToken target, JToken newData)
        {
            if (!UpdateValues(target, newData))
            {
                foreach (var newChildPath in newData.Children())
                {
                    var existingTarget = target[newChildPath.Path];
                    var newChild = newData[newChildPath.Path];

                    // a PATCH of a null object is skipped
                    // use PUT to delete
                    if (newChild.Type == JTokenType.Null)
                    {
                        continue;
                    }

                    JValue existingValue = existingTarget as JValue;

                    if (existingValue != null)
                    {
                        JValue newValue = newChild as JValue;
                        if (newValue != null)
                        {
                            existingValue.Replace(newValue);
                            continue;
                        }
                    }

                    target[newChild.Path] = newChild;
                }
            }
        }

        private bool TryGetChild(FirebasePath path, out JToken node)
        {
            node = _root;

            if (node != null)
            {
                foreach (var segment in path.Segments)
                {
                    node = node[segment];
                    if (node == null)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        internal void GoOnline()
        {
            _connection.Connect();
        }

        internal void GoOffline()
        {
            _connection.Disconnect();
        }

        public void Dispose()
        {
            using (_connection) { }
        }

        internal void ExecuteInitial(Subscription sub)
        {
            lock (_lock)
            {
                if (!_initialReceive)
                {
                    _initialSubscriptions.Enqueue(sub);
                    return;
                }
            }

            sub.Process(this);
        }

        internal void SetPriority(FirebasePath path, FirebasePriority priority, FirebaseStatusCallback callback)
        {
            Set(path.Child(".priority"), priority.JsonValue, callback);
        }

        internal void SetWithPriority(FirebasePath path, string value, FirebasePriority priority, FirebaseStatusCallback callback)
        {
            Set(path, value, priority, callback);
        }
    }
}
