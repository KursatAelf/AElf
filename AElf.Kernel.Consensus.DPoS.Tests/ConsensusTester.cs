using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Contracts.Consensus.DPoS;
using AElf.Contracts.Genesis;
using AElf.Cryptography;
using AElf.Cryptography.ECDSA;
using AElf.Kernel.Account.Application;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using AElf.Kernel.Consensus.Application;
using AElf.Kernel.Consensus.DPoS.Application;
using AElf.Kernel.Consensus.Infrastructure;
using AElf.Kernel.EventMessages;
using AElf.Kernel.KernelAccount;
using AElf.Kernel.Miner.Application;
using AElf.Kernel.Node.Application;
using AElf.Kernel.Services;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.Kernel.TransactionPool.Infrastructure;
using AElf.OS.Node.Application;
using AElf.Types.CSharp;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Volo.Abp;
using Volo.Abp.Threading;

namespace AElf.Kernel.Consensus.DPoS.Tests
{
    // ReSharper disable InconsistentNaming
    public class ConsensusTester
    {
        public int ChainId { get; }
        public ECKeyPair CallOwnerKeyPair { get; private set; }

        private readonly IConsensusService _consensusService;

        private readonly IAccountService _accountService;
        
        private readonly IBlockchainService _blockchainService;
        private readonly IBlockchainNodeContextService _blockchainNodeContextService;
        private readonly IBlockchainExecutingService _blockchainExecutingService;
        private readonly IBlockGenerationService _blockGenerationService;

        private ISystemTransactionGenerationService _systemTransactionGenerationService;
        private readonly IBlockExecutingService _blockExecutingService;

        public Chain Chain => AsyncHelper.RunSync(GetChainAsync);

        public bool ScheduleTriggered { get; set; }

        public ConsensusTester(int chainId, ECKeyPair callOwnerKeyPair, List<ECKeyPair> initialMinersKeyPairs,
            bool isBootMiner = false)
        {
            ChainId = (chainId == 0) ? ChainHelpers.ConvertBase58ToChainId("AELF") : chainId;
            CallOwnerKeyPair = callOwnerKeyPair ?? CryptoHelpers.GenerateKeyPair();

            var application =
                AbpApplicationFactory.Create<DPoSConsensusTestAElfModule>(options => { options.UseAutofac(); });
            application.Initialize();

            var transactionExecutingService = application.ServiceProvider.GetService<ITransactionExecutingService>();
            _blockchainNodeContextService = application.ServiceProvider.GetService<IBlockchainNodeContextService>();
            _blockchainService = application.ServiceProvider.GetService<IBlockchainService>();
            _blockExecutingService = application.ServiceProvider.GetService<IBlockExecutingService>();
            _blockGenerationService = application.ServiceProvider.GetService<IBlockGenerationService>();
            _blockchainExecutingService = application.ServiceProvider.GetService<IBlockchainExecutingService>();

            // Mock dpos options.
            var consensusOptions = MockDPoSOptions(initialMinersKeyPairs, isBootMiner);

            // Mock AccountService.
            _accountService = MockAccountService();

            var consensusControlInformation = new ConsensusControlInformation();
            _consensusService = new ConsensusService(
                new DPoSInformationGenerationService(consensusOptions, _accountService, consensusControlInformation),
                _accountService, transactionExecutingService, MockConsensusScheduler(), _blockchainService,
                consensusControlInformation);

            _systemTransactionGenerationService = new SystemTransactionGenerationService(
                new List<ISystemTransactionGenerator> {new ConsensusTransactionGenerator(_consensusService)});

            InitialChain();
        }

        public async Task TriggerConsensusAsync()
        {
            await _consensusService.TriggerConsensusAsync(ChainId);
        }
        
        public async Task<bool> ValidateConsensusAsync(byte[] consensusInformation)
        {
            return await _consensusService.ValidateConsensusAsync(ChainId, Chain.BestChainHash, Chain.BestChainHeight,
                consensusInformation);
        }

        public async Task<byte[]> GetNewConsensusInformationAsync()
        {
            return await _consensusService.GetNewConsensusInformationAsync(ChainId);
        }

        public async Task<IEnumerable<Transaction>> GenerateConsensusTransactionsAsync()
        {
            return await _consensusService.GenerateConsensusTransactionsAsync(ChainId, Chain.BestChainHeight,
                Chain.BestChainHash.Value.Take(4).ToArray());
        }

        public async Task<Block> TimeToMineABlock(List<Transaction> txs)
        {
            if (!ScheduleTriggered)
            {
                return null;
            }
            
            var preBlock = await _blockchainService.GetBestChainLastBlock(ChainId);
            var minerService = BuildMinerService(txs);
            return await minerService.MineAsync(ChainId, preBlock.GetHash(), preBlock.Height,
                DateTime.UtcNow.AddMilliseconds(4000));
        }

        #region Private methods

        private MinerService BuildMinerService(List<Transaction> txs)
        {
            var trs = new List<TransactionReceipt>();

            foreach (var transaction in txs)
            {
                var tr = new TransactionReceipt(transaction)
                {
                    SignatureStatus = SignatureStatus.SignatureValid, RefBlockStatus = RefBlockStatus.RefBlockValid
                };
                trs.Add(tr);
            }

            var mockTxHub = new Mock<ITxHub>();
            mockTxHub.Setup(h => h.GetReceiptsOfExecutablesAsync()).ReturnsAsync(trs);

            return new MinerService(mockTxHub.Object, _accountService, _blockGenerationService,
                _systemTransactionGenerationService, _blockchainService, _blockExecutingService, _consensusService,
                _blockchainExecutingService);
        }
        
        private async Task<Chain> GetChainAsync()
        {
            return await _blockchainService.GetChainAsync(ChainId);
        }
        
        private void InitialChain()
        {
            var transactions = GetGenesisTransactions(ChainId, typeof(BasicContractZero), typeof(ConsensusContract));
            var dto = new OsBlockchainNodeContextStartDto
            {
                BlockchainNodeContextStartDto = new BlockchainNodeContextStartDto
                {
                    ChainId = ChainId,
                    Transactions = transactions
                }
            };
            
            AsyncHelper.RunSync(() => _blockchainNodeContextService.StartAsync(dto.BlockchainNodeContextStartDto));
        }

        private ISystemTransactionGenerationService MockSystemTransactionGenerationService(List<Transaction> systemTxs)
        {
            var mockSystemTransactionGenerationService = new Mock<ISystemTransactionGenerationService>();
            mockSystemTransactionGenerationService.Setup(s =>
                s.GenerateSystemTransactions(It.IsAny<Address>(), It.IsAny<ulong>(), It.IsAny<byte[]>(),
                    It.IsAny<int>())).Returns(systemTxs);

            return mockSystemTransactionGenerationService.Object;
        }
        private IOptionsSnapshot<DPoSOptions> MockDPoSOptions(List<ECKeyPair> initialMinersKeyPairs, bool isBootMiner)
        {
            var consensusOptionsMock = new Mock<IOptionsSnapshot<DPoSOptions>>();
            consensusOptionsMock.Setup(m => m.Value).Returns(new DPoSOptions
            {
                InitialMiners = initialMinersKeyPairs.Select(p => p.PublicKey.ToHex()).ToList(),
                IsBootMiner = isBootMiner,
                MiningInterval = DPoSConsensusConsts.MiningInterval
            });
            
            return consensusOptionsMock.Object;
        }

        private IAccountService MockAccountService()
        {
            var mockAccountService = new Mock<IAccountService>();
            mockAccountService.Setup(s => s.GetPublicKeyAsync()).ReturnsAsync(CallOwnerKeyPair.PublicKey);
            mockAccountService.Setup(s => s.GetAccountAsync())
                .ReturnsAsync(Address.FromPublicKey(CallOwnerKeyPair.PublicKey));

            return mockAccountService.Object;
        }

        private IConsensusScheduler MockConsensusScheduler()
        {
            var consensusSchedulerMock = new Mock<IConsensusScheduler>();
            consensusSchedulerMock.Setup(m => m.NewEvent(It.IsAny<int>(), It.IsAny<BlockMiningEventData>()))
                .Callback(() => ScheduleTriggered = true);
            consensusSchedulerMock.Setup(m => m.CancelCurrentEvent())
                .Callback(() => ScheduleTriggered = false);

            return consensusSchedulerMock.Object;
        }

        private Transaction[] GetGenesisTransactions(int chainId, params Type[] contractTypes)
        {
            return contractTypes.Select(contractType => GetTransactionForDeployment(chainId, contractType)).ToArray();
        }
        
        private Transaction GetTransactionForDeployment(int chainId, Type contractType)
        {
            var zeroAddress = Address.BuildContractAddress(chainId, 0);

            var code = File.ReadAllBytes(contractType.Assembly.Location);
            return new Transaction
            {
                From = zeroAddress,
                To = zeroAddress,
                MethodName = nameof(ISmartContractZero.DeploySmartContract),
                Params = ByteString.CopyFrom(ParamsPacker.Pack(2, code))
            };
        }

        #endregion
    }
}