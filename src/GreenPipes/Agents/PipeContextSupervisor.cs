﻿// Copyright 2012-2018 Chris Patterson
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace GreenPipes.Agents
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Internals.Extensions;


    /// <summary>
    /// Maintains a cached context, which is created upon first use, and recreated whenever a fault is propogated to the
    /// usage.
    /// </summary>
    /// <typeparam name="TContext"></typeparam>
    public class PipeContextSupervisor<TContext> :
        Supervisor,
        ISupervisor<TContext>
        where TContext : class, PipeContext
    {
        static readonly string Caption = $"Cache<{TypeCache<TContext>.ShortName}>";

        readonly ISupervisor _activeSupervisor;
        readonly IPipeContextFactory<TContext> _contextFactory;
        readonly object _contextLock = new object();
        PipeContextHandle<TContext> _context;

        /// <summary>
        /// Create the cache
        /// </summary>
        /// <param name="contextFactory">Factory used to create the underlying and active contexts</param>
        public PipeContextSupervisor(IPipeContextFactory<TContext> contextFactory)
        {
            _contextFactory = contextFactory;

            _activeSupervisor = new Supervisor();
        }

        protected bool HasContext
        {
            get
            {
                lock (_contextLock)
                {
                    return _context != null && _context.IsDisposed == false;
                }
            }
        }

        async Task IPipeContextSource<TContext>.Send(IPipe<TContext> pipe, CancellationToken cancellationToken)
        {
            var activeContext = CreateActiveContext(cancellationToken);

            try
            {
                TContext context = await activeContext.Context.ConfigureAwait(false);

                await pipe.Send(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await activeContext.Faulted(exception).ConfigureAwait(false);

                throw;
            }
            finally
            {
                await activeContext.Stop(cancellationToken).ConfigureAwait(false);

                await activeContext.DisposeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            var scope = context.CreateScope("source");
            scope.Set(new
            {
                Type = Caption,
                HasContext,
            });
        }

        protected override async Task StopSupervisor(StopSupervisorContext context)
        {
            SetCompleted(ActiveAndActualAgentsCompleted(context));

            // stop the active context agents
            await _activeSupervisor.Stop(context).ConfigureAwait(false);

            await Task.WhenAll(context.Agents.Select(x => x.Stop(context))).UntilCompletedOrCanceled(context.CancellationToken).ConfigureAwait(false);

            await Completed.UntilCompletedOrCanceled(context.CancellationToken).ConfigureAwait(false);
        }

        async Task ActiveAndActualAgentsCompleted(StopSupervisorContext context)
        {
            await _activeSupervisor.Completed.ConfigureAwait(false);

            await Task.WhenAll(context.Agents.Select(x => x.Completed)).ConfigureAwait(false);
        }

        IActivePipeContextAgent<TContext> CreateActiveContext(CancellationToken cancellationToken)
        {
            PipeContextHandle<TContext> pipeContextHandle = GetContext();

            return _contextFactory.CreateActiveContext(_activeSupervisor, pipeContextHandle, cancellationToken);
        }

        PipeContextHandle<TContext> GetContext()
        {
            lock (_contextLock)
            {
                if (_context != null && _context.IsDisposed == false)
                    return _context;

                PipeContextHandle<TContext> context = _context = _contextFactory.CreateContext(this);

                void ClearContext(Task task)
                {
                    Interlocked.CompareExchange(ref _context, null, context);
                }

                context.Context.ContinueWith(ClearContext, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);

                SetReady(context.Context);

                return context;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Caption;
        }
    }
}