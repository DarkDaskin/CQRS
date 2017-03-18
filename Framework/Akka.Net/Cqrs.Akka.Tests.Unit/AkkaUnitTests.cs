﻿using System;
using System.Collections.Generic;
using System.Threading;
using cdmdotnet.Logging;
using cdmdotnet.Logging.Configuration;
using cdmdotnet.StateManagement;
using cdmdotnet.StateManagement.Web;
using Cqrs.Akka.Commands;
using Cqrs.Akka.Domain;
using Cqrs.Akka.Events;
using Cqrs.Akka.Tests.Unit.Commands;
using Cqrs.Akka.Tests.Unit.Commands.Handlers;
using Cqrs.Akka.Tests.Unit.Events;
using Cqrs.Akka.Tests.Unit.Events.Handlers;
using Cqrs.Authentication;
using Cqrs.Bus;
using Cqrs.Commands;
using Cqrs.Configuration;
using Cqrs.Domain;
using Cqrs.Domain.Factories;
using Cqrs.Events;
using Cqrs.Ninject.Akka;
using Cqrs.Ninject.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ninject;

namespace Cqrs.Akka.Tests.Unit
{
	[TestClass]
	public class AkkaUnitTests
	{
		internal static IDictionary<Guid, bool> Step1Reached = new Dictionary<Guid, bool>();
		internal static IDictionary<Guid, bool> Step2Reached = new Dictionary<Guid, bool>();
		internal static IDictionary<Guid, bool> Step3Reached = new Dictionary<Guid, bool>();
		internal static IDictionary<Guid, bool> Step4Reached = new Dictionary<Guid, bool>();

		[TestMethod]
		public void SendingCommandsAndEvents_AcrossBusesInMultipleWays_AllWork()
		{
			// Arrange
			var command = new SayHelloWorldCommand();
			Guid correlationId = Guid.NewGuid();
			ICorrelationIdHelper correlationIdHelper = new WebCorrelationIdHelper(new WebContextItemCollectionFactory());
			correlationIdHelper.SetCorrelationId(correlationId);
			ILogger logger = new ConsoleLogger(new LoggerSettings(), correlationIdHelper);
			IConfigurationManager configurationManager = new ConfigurationManager();
			IBusHelper busHelper = new BusHelper(configurationManager);

			var kernel = new StandardKernel();
			kernel.Bind<ILogger>().ToConstant(logger);
			kernel.Bind<IAggregateFactory>().To<AggregateFactory>().InSingletonScope();
			kernel.Bind<IUnitOfWork<Guid>>().To<UnitOfWork<Guid>>().InSingletonScope();
			kernel.Bind<IRepository<Guid>>().To<AkkaRepository<Guid>>().InSingletonScope();
			kernel.Bind<IAkkaRepository<Guid>>().To<AkkaRepository<Guid>>().InSingletonScope();
			kernel.Bind<IEventStore<Guid>>().To<MemoryCacheEventStore<Guid>>().InSingletonScope();
			kernel.Bind<IEventBuilder<Guid>>().To<DefaultEventBuilder<Guid>>().InSingletonScope();
			kernel.Bind<IEventDeserialiser<Guid>>().To<EventDeserialiser<Guid>>().InSingletonScope();
			kernel.Bind<IEventPublisher<Guid>>().To<InProcessBus<Guid>>().InSingletonScope();
			kernel.Bind<IEventReceiver<Guid>>().To<InProcessBus<Guid>>().InSingletonScope();
			kernel.Bind<ICorrelationIdHelper>().ToConstant(correlationIdHelper).InSingletonScope();
			kernel.Bind<IAkkaEventPublisher<Guid>>().To<AkkaEventBus<Guid>>().InSingletonScope();
			kernel.Bind<IAkkaEventPublisherProxy<Guid>>().To<AkkaEventBusProxy<Guid>>().InSingletonScope();
			kernel.Bind<IAkkaCommandSender<Guid>>().To<AkkaCommandBus<Guid>>().InSingletonScope();
			kernel.Bind<ICommandHandlerRegistrar>().To<AkkaCommandBus<Guid>>().InSingletonScope();
			kernel.Bind<IEventHandlerRegistrar>().To<AkkaEventBus<Guid>>().InSingletonScope();
			kernel.Bind<ICommandSender<Guid>>().To<InProcessBus<Guid>>().InSingletonScope();
			kernel.Bind<ICommandPublisher<Guid>>().To<InProcessBus<Guid>>().InSingletonScope();
			kernel.Bind<ICommandReceiver<Guid>>().To<InProcessBus<Guid>>().InSingletonScope();
			kernel.Bind<IConfigurationManager>().ToConstant(configurationManager).InSingletonScope();
			kernel.Bind<IBusHelper>().ToConstant(busHelper).InSingletonScope();
			kernel.Bind<IAuthenticationTokenHelper<Guid>>().To<AuthenticationTokenHelper<Guid>>().InSingletonScope();
			kernel.Bind<IContextItemCollectionFactory>().To<WebContextItemCollectionFactory>().InSingletonScope();

			AkkaNinjectDependencyResolver.Start(kernel);
			var dependencyResolver = (AkkaNinjectDependencyResolver)NinjectDependencyResolver.Current;

			var commandBus = dependencyResolver.Resolve<ICommandHandlerRegistrar>();
			var eventBus = dependencyResolver.Resolve<IEventHandlerRegistrar>();
			var inProcessBus = dependencyResolver.Resolve<InProcessBus<Guid>>();

			var commandBusProxy = new AkkaCommandBusProxy<Guid>(dependencyResolver, correlationIdHelper, dependencyResolver.Resolve<IAuthenticationTokenHelper<Guid>>());
			// Commands handled by Akka.net
			commandBus.RegisterHandler<SayHelloWorldCommand>(new SayHelloWorldCommandHandler(dependencyResolver).Handle);
			commandBus.RegisterHandler<ReplyToHelloWorldCommand>(new ReplyToHelloWorldCommandHandler(dependencyResolver).Handle);
			commandBus.RegisterHandler<EndConversationCommand>(new EndConversationCommandHandler(dependencyResolver).Handle);

			// Events in process
			inProcessBus.RegisterHandler<HelloWorldSaid>(new HelloWorldSaidEventHandler(dependencyResolver.Resolve<IAkkaCommandSender<Guid>>()).Handle);
			inProcessBus.RegisterHandler<ConversationEnded>(new ConversationEndedEventHandler(dependencyResolver.Resolve<IAkkaCommandSender<Guid>>()).Handle);

			// events handled by Akka.net
			eventBus.RegisterHandler<HelloWorldRepliedTo>(new HelloWorldRepliedToEventHandler(dependencyResolver).Handle);
			eventBus.RegisterHandler<HelloWorldRepliedTo>(new HelloWorldRepliedToSendEndConversationCommandEventHandler(dependencyResolver).Handle);

			Step1Reached.Add(correlationId, false);
			Step2Reached.Add(correlationId, false);
			Step3Reached.Add(correlationId, false);
			Step4Reached.Add(correlationId, false);

			// Act
			commandBusProxy.Send(command);

			// Assert
			SpinWait.SpinUntil(() => Step1Reached[correlationId] && Step2Reached[correlationId] && Step3Reached[correlationId] && Step4Reached[correlationId]);

			AkkaNinjectDependencyResolver.Stop();
		}
	}
}