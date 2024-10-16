// This file is part of the ArmoniK project
// 
// Copyright (C) ANEO, 2021-2023. All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License")
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.gRPC.V1.Submitter;
using ArmoniK.DevelopmentKit.Client.Common.Submitter;
using ArmoniK.DevelopmentKit.Common;

using Google.Protobuf.WellKnownTypes;

using JetBrains.Annotations;

using Microsoft.Extensions.Logging;

namespace ArmoniK.DevelopmentKit.Client.Unified.Services;

/// <summary>
///   The class SessionService will be create each time the function CreateSession or OpenSession will
///   be called by client or by the worker.
/// </summary>
[MarkDownDoc]
public class SessionService : BaseClientSubmitter<SessionService>
{
  /// <summary>
  ///   Ctor to instantiate a new SessionService
  ///   This is an object to send task or get Results from a session
  /// </summary>
  public SessionService(ChannelPool                channelPool,
                        [CanBeNull] ILoggerFactory loggerFactory = null,
                        [CanBeNull] TaskOptions    taskOptions   = null,
                        [CanBeNull] Session        session       = null)
    : base(channelPool,
           loggerFactory)
  {
    TaskOptions = taskOptions ?? InitializeDefaultTaskOptions();

    Logger?.LogDebug("Creating Session... ");

    SessionId = session ?? CreateSession(new List<string>
                                         {
                                           taskOptions.PartitionId,
                                         });

    Logger?.LogDebug($"Session Created {SessionId}");
  }

  /// <summary>
  ///   Return the Grpc channel pool
  /// </summary>
  public ChannelPool ChannelPool
    => channelPool_;

  /// <summary>Returns a string that represents the current object.</summary>
  /// <returns>A string that represents the current object.</returns>
  public override string ToString()
  {
    if (SessionId?.Id != null)
    {
      return SessionId?.Id;
    }

    return "Session_Not_ready";
  }

  /// <summary>
  ///   Supply a default TaskOptions
  /// </summary>
  /// <returns>A default TaskOptions object</returns>
  public static TaskOptions InitializeDefaultTaskOptions()
  {
    TaskOptions taskOptions = new()
                              {
                                MaxDuration = new Duration
                                              {
                                                Seconds = 40,
                                              },
                                MaxRetries           = 2,
                                Priority             = 1,
                                EngineType           = EngineType.Unified.ToString(),
                                ApplicationName      = "ArmoniK.DevelopmentKit.Worker.Unified",
                                ApplicationVersion   = "1.X.X",
                                ApplicationNamespace = "ArmoniK.DevelopmentKit.Worker.Unified",
                                ApplicationService   = "FallBackServerAdder",
                              };

    return taskOptions;
  }

  private Session CreateSession(IEnumerable<string> partitionIds)
  {
    using var _ = Logger?.LogFunction();
    var createSessionRequest = new CreateSessionRequest
                               {
                                 DefaultTaskOption = TaskOptions,
                                 PartitionIds =
                                 {
                                   partitionIds,
                                 },
                               };
    var session = channelPool_.WithChannel(channel => new Api.gRPC.V1.Submitter.Submitter.SubmitterClient(channel).CreateSession(createSessionRequest));

    return new Session
           {
             Id = session.SessionId,
           };
  }

  /// <summary>
  ///   Set connection to an already opened Session
  /// </summary>
  /// <param name="session">SessionId previously opened</param>
  public void OpenSession(Session session)
  {
    if (SessionId == null)
    {
      Logger?.LogDebug($"Open Session {session.Id}");
    }

    SessionId = session;
  }

  /// <summary>
  ///   User method to submit task from the client
  ///   Need a client Service. In case of ServiceContainer
  ///   submitterService can be null until the OpenSession is called
  /// </summary>
  /// <param name="payloads">
  ///   The user payload list to execute. General used for subTasking.
  /// </param>
  /// <param name="maxRetries">The number of retry before fail to submit task. Default = 5 retries</param>
  /// <param name="taskOptions">
  ///   TaskOptions argument to override default taskOptions in Session.
  ///   If non null it will override the default taskOptions in SessionService for client or given by taskHandler for worker
  /// </param>
  public IEnumerable<string> SubmitTasks(IEnumerable<byte[]> payloads,
                                         int                 maxRetries  = 5,
                                         TaskOptions         taskOptions = null)
    => SubmitTasksWithDependencies(payloads.Select(payload => new Tuple<byte[], IList<string>>(payload,
                                                                                               null)),
                                   maxRetries,
                                   taskOptions);

  /// <summary>
  ///   User method to submit task from the client
  /// </summary>
  /// <param name="payload">
  ///   The user payload to execute.
  /// </param>
  /// <param name="waitTimeBeforeNextSubmit">The time to wait before 2 single submitTask</param>
  /// <param name="maxRetries">The number of retry before fail to submit task. Default = 5 retries</param>
  /// <param name="taskOptions">
  ///   TaskOptions argument to override default taskOptions in Session.
  ///   If non null it will override the default taskOptions in SessionService for client or given by taskHandler for worker
  /// </param>
  public string SubmitTask(byte[]      payload,
                           int         waitTimeBeforeNextSubmit = 2,
                           int         maxRetries               = 5,
                           TaskOptions taskOptions              = null)
  {
    Thread.Sleep(waitTimeBeforeNextSubmit); // Twice the keep alive
    return SubmitTasks(new[]
                       {
                         payload,
                       },
                       maxRetries,
                       taskOptions)
      .Single();
  }


  /// <summary>
  ///   The method to submit One task with dependencies tasks. This task will wait for
  ///   to start until all dependencies are completed successfully
  /// </summary>
  /// <param name="payload">The payload to submit</param>
  /// <param name="dependencies">A list of task Id in dependence of this created task</param>
  /// <param name="maxRetries">The number of retry before fail to submit task. Default = 5 retries</param>
  /// <param name="taskOptions">
  ///   TaskOptions argument to override default taskOptions in Session.
  ///   If non null it will override the default taskOptions in SessionService for client or given by taskHandler for worker
  /// </param>
  /// <returns>return the taskId of the created task </returns>
  public string SubmitTaskWithDependencies(byte[]        payload,
                                           IList<string> dependencies,
                                           int           maxRetries  = 5,
                                           TaskOptions   taskOptions = null)
    => SubmitTasksWithDependencies(new[]
                                   {
                                     Tuple.Create(payload,
                                                  dependencies),
                                   },
                                   maxRetries,
                                   taskOptions)
      .Single();
#pragma warning restore CS1591
}
