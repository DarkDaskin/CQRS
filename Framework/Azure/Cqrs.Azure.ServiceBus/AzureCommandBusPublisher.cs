﻿#region Copyright
// // -----------------------------------------------------------------------
// // <copyright company="cdmdotnet Limited">
// // 	Copyright cdmdotnet Limited. All rights reserved.
// // </copyright>
// // -----------------------------------------------------------------------
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Cqrs.Authentication;
using Cqrs.Commands;
using Cqrs.Configuration;
using cdmdotnet.Logging;
using Cqrs.Bus;
using Cqrs.Events;
using Cqrs.Infrastructure;
using Microsoft.ServiceBus.Messaging;

namespace Cqrs.Azure.ServiceBus
{
	/// <summary>
	/// A <see cref="ICommandPublisher{TAuthenticationToken}"/> that resolves handlers , executes the handler and then publishes the <see cref="ICommand{TAuthenticationToken}"/> on the public command bus.
	/// </summary>
	public class AzureCommandBusPublisher<TAuthenticationToken> : AzureCommandBus<TAuthenticationToken>, ISendAndWaitCommandSender<TAuthenticationToken>
	{
		public AzureCommandBusPublisher(IConfigurationManager configurationManager, IMessageSerialiser<TAuthenticationToken> messageSerialiser, IAuthenticationTokenHelper<TAuthenticationToken> authenticationTokenHelper, ICorrelationIdHelper correlationIdHelper, ILogger logger, IAzureBusHelper<TAuthenticationToken> azureBusHelper, BusHelper busHelper)
			: base(configurationManager, messageSerialiser, authenticationTokenHelper, correlationIdHelper, logger, azureBusHelper, busHelper, true)
		{
		}

		#region Implementation of ICommandSender<TAuthenticationToken>

		public virtual void Publish<TCommand>(TCommand command)
			where TCommand : ICommand<TAuthenticationToken>
		{
			if (!AzureBusHelper.PrepareAndValidateCommand(command, "Azure-ServiceBus"))
				return;

			try
			{
				var brokeredMessage = new BrokeredMessage(MessageSerialiser.SerialiseCommand(command))
				{
					CorrelationId = CorrelationIdHelper.GetCorrelationId().ToString("N")
				};
				brokeredMessage.Properties.Add("Type", command.GetType().FullName);
				PrivateServiceBusPublisher.Send(brokeredMessage);
			}
			catch (QuotaExceededException exception)
			{
				Logger.LogError("The size of the command being sent was too large.", exception: exception, metaData: new Dictionary<string, object> { { "Command", command } });
				throw;
			}
			catch (Exception exception)
			{
				Logger.LogError("An issue occurred while trying to publish a command.", exception: exception, metaData: new Dictionary<string, object> { { "Command", command } });
				throw;
			}
			Logger.LogInfo(string.Format("A command was sent of type {0}.", command.GetType().FullName));
		}

		public virtual void Send<TCommand>(TCommand command)
			where TCommand : ICommand<TAuthenticationToken>
		{
			Publish(command);
		}

		public virtual void Publish<TCommand>(IEnumerable<TCommand> commands)
			where TCommand : ICommand<TAuthenticationToken>
		{
			IList<TCommand> sourceCommands = commands.ToList();
			IList<string> sourceCommandMessages = new List<string>();
			IList<BrokeredMessage> brokeredMessages = new List<BrokeredMessage>(sourceCommands.Count);
			foreach (TCommand command in sourceCommands)
			{
				if (!AzureBusHelper.PrepareAndValidateCommand(command, "Azure-ServiceBus"))
					continue;

				var brokeredMessage = new BrokeredMessage(MessageSerialiser.SerialiseCommand(command))
				{
					CorrelationId = CorrelationIdHelper.GetCorrelationId().ToString("N")
				};
				brokeredMessage.Properties.Add("Type", command.GetType().FullName);

				brokeredMessages.Add(brokeredMessage);
				sourceCommandMessages.Add(string.Format("A command was sent of type {0}.", command.GetType().FullName));
			}

			try
			{
				PrivateServiceBusPublisher.SendBatch(brokeredMessages);
			}
			catch (QuotaExceededException exception)
			{
				Logger.LogError("The size of the command being sent was too large.", exception: exception, metaData: new Dictionary<string, object> { { "Commands", sourceCommands } });
				throw;
			}
			catch (Exception exception)
			{
				Logger.LogError("An issue occurred while trying to publish a command.", exception: exception, metaData: new Dictionary<string, object> { { "Commands", sourceCommands } });
				throw;
			}

			foreach (string message in sourceCommandMessages)
				Logger.LogInfo(message);
		}

		public virtual void Send<TCommand>(IEnumerable<TCommand> commands)
			where TCommand : ICommand<TAuthenticationToken>
		{
			Publish(commands);
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits for an event of <typeparamref name="TEvent"/>
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, IEventReceiver<TAuthenticationToken> eventReceiver = null)
			where TCommand : ICommand<TAuthenticationToken>
		{
			return SendAndWait<TCommand, TEvent>(command, -1, eventReceiver);
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits for an event of <typeparamref name="TEvent"/> or exits if the specified timeout is expired.
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="F:System.Threading.Timeout.Infinite"/> (-1) to wait indefinitely.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, int millisecondsTimeout, IEventReceiver<TAuthenticationToken> eventReceiver = null)
			where TCommand : ICommand<TAuthenticationToken>
		{
			return SendAndWait(command, events => (TEvent)events.SingleOrDefault(@event => @events is TEvent), millisecondsTimeout, eventReceiver);
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits for an event of <typeparamref name="TEvent"/> or exits if the specified timeout is expired.
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="timeout">A <see cref="T:System.TimeSpan"/> that represents the number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, TimeSpan timeout, IEventReceiver<TAuthenticationToken> eventReceiver = null)
			where TCommand : ICommand<TAuthenticationToken>
		{
			long num = (long)timeout.TotalMilliseconds;
			if (num < -1L || num > int.MaxValue)
				throw new ArgumentOutOfRangeException("timeout", timeout, "SpinWait_SpinUntil_TimeoutWrong");
			return SendAndWait<TCommand, TEvent>(command, (int)timeout.TotalMilliseconds, eventReceiver);
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits until the specified condition is satisfied an event of <typeparamref name="TEvent"/>
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="condition">A delegate to be executed over and over until it returns the <typeparamref name="TEvent"/> that is desired, return null to keep trying.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, Func<IEnumerable<IEvent<TAuthenticationToken>>, TEvent> condition, IEventReceiver<TAuthenticationToken> eventReceiver = null)
			where TCommand : ICommand<TAuthenticationToken>
		{
			return SendAndWait(command, condition, -1, eventReceiver);
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits for an event of <typeparamref name="TEvent"/> or exits if the specified timeout is expired.
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="condition">A delegate to be executed over and over until it returns the <typeparamref name="TEvent"/> that is desired, return null to keep trying.</param>
		/// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="F:System.Threading.Timeout.Infinite"/> (-1) to wait indefinitely.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, Func<IEnumerable<IEvent<TAuthenticationToken>>, TEvent> condition, int millisecondsTimeout,
			IEventReceiver<TAuthenticationToken> eventReceiver = null) where TCommand : ICommand<TAuthenticationToken>
		{
			if (eventReceiver != null)
				throw new NotSupportedException("Specifying a different event receiver is not yet supported.");
			if (!AzureBusHelper.PrepareAndValidateCommand(command, "Azure-ServiceBus"))
				return (TEvent)(object)null;

			TEvent result = (TEvent)(object)null;
			EventWaits.Add(command.CorrelationId, new List<IEvent<TAuthenticationToken>>());

			try
			{
				var brokeredMessage = new BrokeredMessage(MessageSerialiser.SerialiseCommand(command))
				{
					CorrelationId = CorrelationIdHelper.GetCorrelationId().ToString("N")
				};
				brokeredMessage.Properties.Add("Type", command.GetType().FullName);
				PrivateServiceBusPublisher.Send(brokeredMessage);
			}
			catch (QuotaExceededException exception)
			{
				Logger.LogError("The size of the command being sent was too large.", exception: exception, metaData: new Dictionary<string, object> { { "Command", command } });
				throw;
			}
			catch (Exception exception)
			{
				Logger.LogError("An issue occurred while trying to publish a command.", exception: exception, metaData: new Dictionary<string, object> { { "Command", command } });
				throw;
			}
			Logger.LogInfo(string.Format("A command was sent of type {0}.", command.GetType().FullName));

			SpinWait.SpinUntil(() =>
			{
				IList<IEvent<TAuthenticationToken>> events = EventWaits[command.CorrelationId];

				result = condition(events);

				return result != null;
			}, millisecondsTimeout, sleepInMilliseconds: 1000);

			return result;
		}

		/// <summary>
		/// Sends the provided <paramref name="command"></paramref> and waits for an event of <typeparamref name="TEvent"/> or exits if the specified timeout is expired.
		/// </summary>
		/// <param name="command">The <typeparamref name="TCommand"/> to send.</param>
		/// <param name="condition">A delegate to be executed over and over until it returns the <typeparamref name="TEvent"/> that is desired, return null to keep trying.</param>
		/// <param name="timeout">A <see cref="T:System.TimeSpan"/> that represents the number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely.</param>
		/// <param name="eventReceiver">If provided, is the <see cref="IEventReceiver{TAuthenticationToken}" /> that the event is expected to be returned on.</param>
		public virtual TEvent SendAndWait<TCommand, TEvent>(TCommand command, Func<IEnumerable<IEvent<TAuthenticationToken>>, TEvent> condition, TimeSpan timeout, IEventReceiver<TAuthenticationToken> eventReceiver = null)
			where TCommand : ICommand<TAuthenticationToken>
		{
			long num = (long)timeout.TotalMilliseconds;
			if (num < -1L || num > int.MaxValue)
				throw new ArgumentOutOfRangeException("timeout", timeout, "SpinWait_SpinUntil_TimeoutWrong");
			return SendAndWait(command, condition, (int)timeout.TotalMilliseconds, eventReceiver);
		}

		#endregion
	}
}