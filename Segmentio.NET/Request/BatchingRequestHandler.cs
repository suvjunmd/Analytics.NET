﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization.Json;
using System.IO;

using Segmentio.Model;
using Segmentio.Trigger;

namespace Segmentio.Request
{
    internal class BatchingRequestHandler : IRequestHandler
    {
        
        private string apiKey;
        private Queue<BaseAction> queue;
        private DateTime lastFlush;

        private int batchIncrement;
        private int maxSize;

        private Client client;
        private IFlushTrigger[] triggers;

        public BatchingRequestHandler(IFlushTrigger[] triggers)
        {
            queue = new Queue<BaseAction>();

            this.triggers = triggers;
            
            this.batchIncrement = 50;
            this.maxSize = 1000000;
        }

        public void Initialize(Client client, string apiKey)
        {
            this.client = client;
            this.apiKey = apiKey;
        }

        public void Process(BaseAction action)
        {
            int size = queue.Count;

            if (size > maxSize)
            {
                // drop the message
                // TODO: log it
            }
            else
            {
                lock (queue)
                {
                    queue.Enqueue(action);
                }

                client.Statistics.Submitted += 1;
            }

            foreach (IFlushTrigger trigger in triggers)
            {
                if (trigger.shouldFlush(lastFlush, size))
                {
                    Flush();
                    break;
                }
            }
        }

        public void Flush()
        {
            List<BaseAction> actions = new List<BaseAction>();

            lock (queue) 
            {
                for (int i = 0; i < batchIncrement; i += 1)
                {
                    if (queue.Count == 0) break;

                    BaseAction action = queue.Dequeue();
                    actions.Add(action);
                }
            }

            if (actions.Count > 0)
            {
                Batch batch = new Batch(apiKey, actions);
                MakeRequest(batch);

                lastFlush = DateTime.Now;
            }
        }

        private void MakeRequest(Batch batch)
        {
            try
            {
                Uri uri = new Uri(Segmentio._Protocol + Segmentio._Host + Segmentio._Endpoints["batch"]);

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Batch));

                // Create a request
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);

                request.ContentType = "application/json";
                request.Method = "POST";
                // disable following 302 redirect
                request.AllowAutoRedirect = false;
                // do not use hte expect 100-continue behavior
                request.ServicePoint.Expect100Continue = false;
                // buffer the data before sending, ok since we send all in one shot
                request.AllowWriteStreamBuffering = true;
                using (var requestStream = request.GetRequestStream())
                {
                    // serialize the report to request
                    serializer.WriteObject(requestStream, batch);
                }

                BatchState state = new BatchState(request, batch);

                request.BeginGetResponse(FinishWebRequest, state);
            }
            catch (System.Exception e)
            {
                foreach (BaseAction action in batch.batch)
                {
                    client.Statistics.Failed += 1;
                    client.RaiseFailure(action, e.Message);
                }
            }
        }

        private void FinishWebRequest(IAsyncResult result)
        {
            BatchState state = (BatchState) result.AsyncState;
            HttpWebRequest request = state.Request;
            try
            {
                using (var response = (HttpWebResponse)request.EndGetResponse(result))
                {

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // log success
                        foreach (BaseAction action in state.Batch.batch)
                        {
                            client.Statistics.Succeeded += 1;
                            client.RaiseSuccess(action);
                        }
                    }
                    else
                    {
                        string responseStr = String.Format("Status Code {0}. ", response.StatusCode);

                        using (Stream responseStream = response.GetResponseStream())
                        {
                            using (StreamReader reader = new StreamReader(responseStream))
                            {
                                responseStr += reader.ReadToEnd();
                            }
                        }


                        foreach (BaseAction action in state.Batch.batch)
                        {
                            client.Statistics.Failed += 1;
                            client.RaiseFailure(action, responseStr);
                        }
                    }
                }
            }
            catch (WebException e)
            {
                foreach (BaseAction action in state.Batch.batch)
                {
                    client.Statistics.Failed += 1;
                    client.RaiseFailure(action, e.Message);
                }
            }
        }
    }
}

