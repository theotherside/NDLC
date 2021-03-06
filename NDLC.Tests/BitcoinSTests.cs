﻿using NBitcoin.DataEncoders;
using NDLC.Messages;
using NDLC.Secp256k1;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using NBitcoin;
using Xunit.Abstractions;
using NBitcoin.Logging;
using Xunit.Sdk;
using NBitcoin.Crypto;

namespace NDLC.Tests
{
	public class BitcoinSTests
	{
		public BitcoinSTests(ITestOutputHelper testOutputHelper)
		{
			this.testOutputHelper = testOutputHelper;
		}
		static JsonSerializerSettings _Settings;
		static JsonSerializerSettings Settings
		{
			get
			{
				if (_Settings is null)
				{
					var settings = new JsonSerializerSettings();
					settings.Formatting = Formatting.Indented;
					Messages.Serializer.Configure(settings, Network.RegTest);
					_Settings = settings;
				}
				return _Settings;
			}
		}
		static JsonSerializerSettings _TestnetSettings;
		private readonly ITestOutputHelper testOutputHelper;

		static JsonSerializerSettings TestnetSettings
		{
			get
			{
				if (_TestnetSettings is null)
				{
					var settings = new JsonSerializerSettings();
					settings.Formatting = Formatting.Indented;
					Messages.Serializer.Configure(settings, Network.TestNet);
					_TestnetSettings = settings;
				}
				return _TestnetSettings;
			}
		}

		[Fact]
		public void CanValidateCETSigs()
		{
			var offer = Parse<Messages.Offer>("Data/Offer2.json");
			var accept = Parse<Messages.Accept>("Data/Accept2.json");
			var sign = Parse<Messages.Sign>("Data/Sign2.json");

			var funding = Transaction.Parse("02000000000102118df2c93e3be1f05af4fe16ba82eab135ba624bb96db5b218222ad7f99f1c400000000000ffffffff9ff82cc6f2509b2bc9f0d56fa37bea855d40b74cd2633e75ca343469508e892d0000000000ffffffff0372dc010000000000220020a9dbe7e77d7f966d4b8ab36d576e19f4c392caf039ea5e53a0078d771a3e92eefb2a042a01000000160014ec8d64ed4742016d12deac1106577f94747f09f41b5afa2f01000000160014c22a6d286c108ad458ba60e00576859a78ae8de002473044022071e5e088ba8e4434ab044350448479561ad8179bf2a88789a9459215bcf610c80220128535940b083dca52791f373b6474dd24f3a9096c039411943ffcff6fb293a801210333dea605a7d2c223de68c1189bd5e171cb94bd35712122eff08a9cdbed5dca8d02473044022059d6a3ae8cee036998065d2139459a029c4d925f952a860434910b12f9e9a3a002205871d75c28b8486caa90a6d44c3ae59eeb98bbbc90990f5b510466178eb80d9c01210310f8a8f195fe3167af0db8c6bb208e9a8ac075c99276a0150c1d374d0d13d26a00000000", Network.Main);
			var cet = Transaction.Parse("020000000001016b3ea350dfa327d9c4c030fc3e2e49c04aae28d6020f1624202c57bf620dddbc0000000000feffffff01a08601000000000016001404bea358f0a1c3bdabe6148fd2ea44838110ef830400483045022100c3d1901ae606851946edaaa007cc5ed3ba05a1a812e76d8e4f5a3b034fe11dd4022063d51c44bc8a78ee752e735e3c84be2329e0eb15022db552a1181eaf1edb7dff0147304402205f5ba5027909abef13c61bc604eb2deaf223549213c6c7c9456d972ae1badf800220348bf4918b01bea8ee69d104a60254ad23159654a8f8b31e43da1fc53603c2390147522102d63fec4b8a6705a34b32e5476a5a07c20774fe2abcf88eeea320c9714bc3010e21038b22637639db00f3dd5d8c46644b61954a01362bc92a87eb136bd6ba80c1f12852ae64000000", Network.Main);
			RemoveSigs(cet);
			RemoveSigs(funding);


			foreach (var isInitiator in new[] { true, false })
			{
				var b = new DLCTransactionBuilder(isInitiator, offer, accept, sign, funding, Network.RegTest);
				var actualCet = b.BuildCET(offer.ContractInfo[1].Outcome);
				Assert.Equal(cet.ToString(), actualCet.ToString());
				Assert.True(b.VerifyRemoteCetSigs(isInitiator ? accept.CetSigs.OutcomeSigs : sign.CetSigs.OutcomeSigs));
				Assert.True(b.VerifyRemoteRefundSignature());

				b = new DLCTransactionBuilder(isInitiator, offer, accept, sign, Network.RegTest);
				var actualFunding = b.GetFundingTransaction();
				RemoveSigs(actualFunding);
				Assert.Equal(funding.ToString(), actualFunding.ToString());
			}

		}

		private static void RemoveSigs(Transaction cet)
		{
			foreach (var input in cet.Inputs)
				input.WitScript = WitScript.Empty;
		}

		[Fact]
		public void CanComputeSigPoint()
		{
			for (int i = 0; i < 100; i++)
			{
				var oracleKey = Context.Instance.CreateECPrivKey(RandomUtils.GetBytes(32));
				var msg = RandomUtils.GetBytes(32);
				var kValue = Context.Instance.CreateECPrivKey(RandomUtils.GetBytes(32));
				var nonce = kValue.CreateSchnorrNonce();
				var sig = oracleKey.SignBIP140(msg, new PrecomputedNonceFunctionHardened(kValue.ToBytes()));
				Assert.Equal(sig.rx, nonce.PubKey.Q.x);
				Assert.True(oracleKey.CreateXOnlyPubKey().TryComputeSigPoint(msg, nonce, out var sigPoint));
				Assert.Equal(sigPoint.Q, Context.Instance.CreateECPrivKey(sig.s).CreatePubKey().Q);
			}
		}

		[Fact]
		public void FullExchange()
		{
			var offerExample = Parse<Messages.Offer>("Data/Offer2.json");
			var offerKey = new Key();
			var acceptKey = new Key();
			var initiatorInputKey = new Key();
			var acceptorInputKey = new Key();
			var initiator = new DLCTransactionBuilder(true, null, null, null, Network.RegTest);
			var requiredFund = initiator.Offer(offerExample.OracleInfo.PubKey,
								  offerExample.OracleInfo.RValue,
								  DiscretePayoffs.CreateFromContractInfo(offerExample.ContractInfo, offerExample.TotalCollateral,
								  new[] { new DiscreteOutcome("Republicans_win"), new DiscreteOutcome("Democrats_win"), new DiscreteOutcome("other") }),
								  offerExample.Timeouts);
			var fund1 = GetFundingPSBT(initiatorInputKey, requiredFund);
			var offer = initiator.FundOffer(offerKey, fund1);
			var acceptor = new DLCTransactionBuilder(false, null, null, null, Network.RegTest);
			var acceptorPayoff = acceptor.Accept(offer);
			var fund2 = GetFundingPSBT(acceptorInputKey, acceptorPayoff.CalculateMinimumCollateral());
			var accept = acceptor.FundAccept(acceptKey, fund2);
			initiator.Sign1(accept);
			var fundPSBT = initiator.GetFundingPSBT();
			fundPSBT.SignWithKeys(initiatorInputKey);
			var sign = initiator.Sign2(offerKey, fundPSBT);

			acceptor.Finalize1(sign);
			fundPSBT = acceptor.GetFundingPSBT();
			fundPSBT.SignWithKeys(acceptorInputKey);
			var fullyVerified = acceptor.Finalize(fundPSBT);
			foreach (var i in fullyVerified.Inputs)
				Assert.NotNull(i.WitScript);
			fundPSBT = acceptor.GetFundingPSBT();
			if (fundPSBT.TryGetEstimatedFeeRate(out var estimated))
				Assert.True(estimated > new FeeRate(1.0m), "Fee Rate of the funding PSBT are too low");
		}

		[Fact]
		public void CanConvertContractInfoToPayoff()
		{
			var payoffs = new DiscretePayoffs();
			payoffs.Add(new DiscreteOutcome("a"), Money.Coins(5.0m));
			payoffs.Add(new DiscreteOutcome("b"), Money.Coins(-5.0m));
			payoffs.Add(new DiscreteOutcome("c"), Money.Coins(-2.0m));
			Assert.Equal(Money.Coins(5.0m), payoffs.CalculateMinimumCollateral());
			var ci = payoffs.ToContractInfo(payoffs.CalculateMinimumCollateral());
			Assert.Equal(Money.Coins(10.0m), ci[0].Payout);
			Assert.Equal(Money.Coins(0m), ci[1].Payout);
			Assert.Equal(Money.Coins(3.0m), ci[2].Payout);

			payoffs = DiscretePayoffs.CreateFromContractInfo(ci, Money.Coins(5.0m));
			Assert.Equal(Money.Coins(5.0m), payoffs[0].Reward);
			Assert.Equal(Money.Coins(-5.0m), payoffs[1].Reward);
			Assert.Equal(Money.Coins(-2.0m), payoffs[2].Reward);
		}

		[Fact]
		public void CanGenerateSchnorrNonce()
		{
			for (int i = 0; i < 30; i++)
			{
				var privKey = new Key().ToECPrivKey();
				var nonce = privKey.CreateSchnorrNonce();
				var msg = RandomUtils.GetBytes(32);
				privKey.TrySignBIP140(msg, new PrecomputedNonceFunctionHardened(privKey.ToBytes()), out var sig);
				//Assert.Equal(sig.rx, nonce.fe);
				Assert.True(privKey.CreateXOnlyPubKey().SigVerifyBIP340(sig, msg));
			}
		}
		[Fact]
		public void FullExchange2()
		{
			var initiatorInputKey = new Key();
			var acceptorInputKey = new Key();

			var offerKey = new Key();
			var acceptKey = new Key();

			var initiator = new DLCTransactionBuilder(true, null, null, null, Network.RegTest);
			var acceptor = new DLCTransactionBuilder(false, null, null, null, Network.RegTest);

			var oracleInfo = OracleInfo.Parse("e4d36e995ff4bba4da2b60ad907d61d36e120d6f7314a3c2a20c6e27a5cd850ff67f8f41718c86f05eb95fab308f5ed788a2a963124299154648f97124caa579");
			var requiredCollateral = initiator.Offer(oracleInfo.PubKey, oracleInfo.RValue,
				new DiscretePayoffs() {
				new DiscretePayoff("Republicans", Money.Coins(0.4m)),
				new DiscretePayoff("Democrats", -Money.Coins(0.6m)),
				new DiscretePayoff("Smith", Money.Zero) }, new Timeouts()
				{
					ContractMaturity = 100,
					ContractTimeout = 200
				});
			var fund1 = GetFundingPSBT(acceptorInputKey, requiredCollateral);
			var offer = initiator.FundOffer(offerKey, fund1);

			var payoff = acceptor.Accept(offer);
			var fund2 = GetFundingPSBT(initiatorInputKey, payoff.CalculateMinimumCollateral());
			var accept = acceptor.FundAccept(acceptKey, fund2);
			initiator.Sign1(accept);
			var fundPSBT = initiator.GetFundingPSBT();
			fundPSBT.SignWithKeys(initiatorInputKey);
			var sign = initiator.Sign2(offerKey, fundPSBT);

			acceptor.Finalize1(sign);
			fundPSBT = acceptor.GetFundingPSBT();
			fundPSBT.SignWithKeys(acceptorInputKey);
			var fullyVerified = acceptor.Finalize(fundPSBT);
			foreach (var i in fullyVerified.Inputs)
				Assert.NotNull(i.WitScript);

			var cet = initiator.BuildCET(offer.ContractInfo[0].Outcome);
			var keyBytes = Encoders.Hex.DecodeData("d1da46f96f0be50bce2bbabe6bc8633f448ec3f1d14715a0b086b68ed34e095d");
			var oracleSecret = new Key(keyBytes);

			var sig = oracleInfo.RValue.CreateSchnorrSignature(oracleSecret.ToECPrivKey());
			Assert.True(oracleInfo.PubKey.SigVerifyBIP340(sig, new DiscreteOutcome("Republicans").Hash));

			var execution = initiator.Execute(offerKey, oracleSecret);
			// Can extract?
			initiator.ExtractAttestation(execution.CET);
			acceptor.ExtractAttestation(execution.CET);

			this.testOutputHelper.WriteLine("----Final state------");
			testOutputHelper.WriteLine(JObject.Parse(initiator.ExportState()).ToString(Formatting.Indented));
			this.testOutputHelper.WriteLine("---------------------");
		}

		[Fact]
		public void CanCalculateEventId()
		{
			var offer = Parse<Messages.Offer>("Data/Offer2.json");
			var accept = Parse<Messages.Accept>("Data/Accept2.json");

			var builder = new DLCTransactionBuilder(false, null, null, null, Network.RegTest);
			var payoff = builder.Accept(offer);
			PSBT fundPSBT = GetFundingPSBT(new Key(), payoff.CalculateMinimumCollateral());
			var accept2 = builder.FundAccept(new Key(), fundPSBT);
			Assert.Equal(accept.EventId, accept2.EventId);
		}

		[Fact]
		public void CanCreateAccept()
		{
			var offer = Parse<Messages.Offer>("Data/Offer2.json");
			offer.SetContractPreimages(
				new DiscreteOutcome("Republicans_win"),
				new DiscreteOutcome("Democrats_win"),
				new DiscreteOutcome("other")
				);
			var builder = new DLCTransactionBuilder(false, null, null, null, Network.RegTest);
			var fundingInputKey = new Key();
			var payoffs = builder.Accept(offer);
			PSBT fundPSBT = GetFundingPSBT(fundingInputKey, payoffs.CalculateMinimumCollateral());
			var accept = builder.FundAccept(new Key(), fundPSBT);

			builder = new DLCTransactionBuilder(true, offer, null, null, Network.RegTest);
			builder.Sign1(accept);
		}
		private static PSBT GetFundingPSBT(Key ownedCoinKey, Money collateral)
		{
			var c1 = new Coin(new OutPoint(RandomUtils.GetUInt256(), 0), new TxOut(Money.Coins(2.0m), ownedCoinKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit)));
			var txbuilder = Network.RegTest.CreateTransactionBuilder();
			txbuilder.AddCoins(c1);
			txbuilder.Send(new Key().ScriptPubKey, collateral);
			txbuilder.SendEstimatedFees(new FeeRate(1.0m));
			txbuilder.SetChange(new Key().ScriptPubKey);
			var fundPSBT = txbuilder.BuildPSBT(false);
			return fundPSBT;
		}

		[Fact]
		public void CanCheckMessages()
		{
			var offer = Parse<Messages.Offer>("Data/Offer.json");
			Assert.Equal("SHA256:cbaede9e2ad17109b71b85a23306b6d4b93e78e8e8e8d830d836974f16128ae8", offer.ContractInfo[1].Outcome.ToString());
			Assert.Equal(200000000L, offer.ContractInfo[1].Payout.Satoshi);
			Assert.Equal(100000000L, offer.TotalCollateral.Satoshi);

			var accept = Parse<Messages.Accept>("Data/Accept.json");
			Assert.Equal(100000000L, accept.TotalCollateral.Satoshi);
			Assert.Equal(2, accept.CetSigs.OutcomeSigs.Count);
			Assert.Equal("00595165a73cc04eaab13077abbffae5edf0c371b9621fad9ea28da00026373a853bcc3ac24939d0d004e39b96469b2173aa20e429ca3bffd3ab0db7735ad6d87a012186ff2afb8c05bca05ad8acf22aecadf47f967bb81753c13c3b081fc643c8db855283e554359d1a1a870d2b016a9db6e6838f5ca1afb1508aa0c50fd9d05ac60a7b7cc2570b62426d467183baf109fb23a5fdf37f273c087c23744c6529f353", accept.CetSigs.OutcomeSigs[new DiscreteOutcome(Encoders.Hex.DecodeData("1bd3f7beb217b55fd40b5ea7e62dc46e6428c15abd9e532ac37604f954375526"))].ToString());
			var str = JsonConvert.SerializeObject(accept, Settings);
			accept = JsonConvert.DeserializeObject<Accept>(str, Settings);
			Assert.Equal("00595165a73cc04eaab13077abbffae5edf0c371b9621fad9ea28da00026373a853bcc3ac24939d0d004e39b96469b2173aa20e429ca3bffd3ab0db7735ad6d87a012186ff2afb8c05bca05ad8acf22aecadf47f967bb81753c13c3b081fc643c8db855283e554359d1a1a870d2b016a9db6e6838f5ca1afb1508aa0c50fd9d05ac60a7b7cc2570b62426d467183baf109fb23a5fdf37f273c087c23744c6529f353", accept.CetSigs.OutcomeSigs[new DiscreteOutcome(Encoders.Hex.DecodeData("1bd3f7beb217b55fd40b5ea7e62dc46e6428c15abd9e532ac37604f954375526"))].ToString());

			var sign = Parse<Messages.Sign>("Data/Sign.json");
			var sig = sign.FundingSigs[OutPoint.Parse("e7d8c121f888631289b14989a07e90bcb8c53edf88d5d3ee978fb75b382f26d102000000")][0].ToString(); ;
			Assert.Equal("220202f37b2ca55f880f9d73b311a4369f2f02fbadefc037628d6eaef98ec222b8bcb046304302206589a41139774c27c242af730ae225483ba264eeec026952c6a6cd0bc8a7413c021f2289c32cfb7b4baa5873e350675133de93cfc69de50220fddafbc6f23a46e201", sig);

			CanRoundTrip<Messages.Accept>("Data/Accept.json");
			CanRoundTrip<Messages.Offer>("Data/Offer.json");
			CanRoundTrip<Messages.Sign>("Data/Sign.json");
		}

		private void CanRoundTrip<T>(string file)
		{
			var content = File.ReadAllText(file);
			var data = JsonConvert.DeserializeObject<T>(content, Settings);
			var back = JsonConvert.SerializeObject(data, Settings);
			var expected = JObject.Parse(content);
			var actual = JObject.Parse(back);
			Assert.Equal(expected.ToString(Formatting.Indented), actual.ToString(Formatting.Indented));
		}

		private static T Parse<T>(string file, JsonSerializerSettings settings = null)
		{
			var content = File.ReadAllText(file);
			return JsonConvert.DeserializeObject<T>(content, settings ?? Settings);
		}

		[Fact]
		public void testAdaptorSign()
		{

			byte[] msg = toByteArray("024BDD11F2144E825DB05759BDD9041367A420FAD14B665FD08AF5B42056E5E2");
			byte[] adaptor = toByteArray("038D48057FC4CE150482114D43201B333BF3706F3CD527E8767CEB4B443AB5D349");
			byte[] seckey = toByteArray("90AC0D5DC0A1A9AB352AFB02005A5CC6C4DF0DA61D8149D729FF50DB9B5A5215");
			String expectedAdaptorSig = "00CBE0859638C3600EA1872ED7A55B8182A251969F59D7D2DA6BD4AFEDF25F5021A49956234CBBBBEDE8CA72E0113319C84921BF1224897A6ABD89DC96B9C5B208";
			String expectedAdaptorProof = "00B02472BE1BA09F5675488E841A10878B38C798CA63EFF3650C8E311E3E2EBE2E3B6FEE5654580A91CC5149A71BF25BCBEAE63DEA3AC5AD157A0AB7373C3011D0FC2592A07F719C5FC1323F935569ECD010DB62F045E965CC1D564EB42CCE8D6D";

			byte[] resultArr = adaptorSign(seckey, adaptor, msg);

			assertEquals(resultArr.Length, 162, "testAdaptorSign");

			String adaptorSig = toHex(resultArr);
			assertEquals(adaptorSig, expectedAdaptorSig + expectedAdaptorProof, "testAdaptorSign");
		}

		private void assertEquals<T>(T actual, T expected, string message)
		{
			Assert.Equal(expected, actual);
		}

		private string toHex(byte[] resultArr)
		{
			return Encoders.Hex.EncodeData(resultArr).ToUpperInvariant();
		}

		class AcceptorTest
		{
			public static AcceptorTest Open(string folder, Network network)
			{
				var settings = network == Network.RegTest ? Settings : TestnetSettings;
				AcceptorTest t = new AcceptorTest();
				var fundingOverride = Path.Combine(folder, "FundingOverride.hex");
				if (File.Exists(fundingOverride))
				{
					t.FundingOverride = Transaction.Parse(File.ReadAllText(fundingOverride), network);
				}
				t.Offer = Parse<Offer>(Path.Combine(folder, "Offer.json"), settings);
				t.Sign = Parse<Sign>(Path.Combine(folder, "Sign.json"), settings);

				var attestation = Path.Combine(folder, "OracleAttestation.hex");
				if (File.Exists(attestation))
				{
					t.OracleAttestation = new Key(Encoders.Hex.DecodeData(File.ReadAllText(attestation)));
				}
				t.Builder = new DLCTransactionBuilder(false, null, null, null, network);
				t.FundingTemplate = PSBT.Parse(File.ReadAllText(Path.Combine(folder, "FundingTemplate.psbt")), network);
				return t;
			}
			public Transaction FundingOverride { get; set; }
			public Offer Offer { get; set; }
			public Sign Sign { get; set; }
			public Key OracleAttestation { get; set; }

			public DLCTransactionBuilder Builder { get; set; }
			/// <summary>
			/// Funding templates are PSBT built with the following format:
			/// * 1 output sending to "collateral" BTC to the payout address
			/// * Optionally, 1 output which is the change address
			/// </summary>
			public PSBT FundingTemplate { get; set; }
		}

		[Fact]
		public void AcceptorTestVectors()
		{
			RunAcceptorTest("Data/Acceptor-Chris", Network.TestNet);
			RunAcceptorTest("Data/Acceptor-Chris2", Network.TestNet);
		}
		void RunAcceptorTest(string acceptorFolder, Network network)
		{
			var myKey = new Key(Encoders.Hex.DecodeData("659ed592d16a47f8b3eea2e3d918624963e20da29a83e097c0ffbf0ef1b3e8cc"));
			var data = AcceptorTest.Open(acceptorFolder, network);

			if (data.FundingOverride != null)
			{
				testOutputHelper.WriteLine("Using funding override");
				data.Builder.FundingOverride = data.FundingOverride;
			}

			data.Builder.Accept(data.Offer, data.FundingTemplate.Outputs[0].Value);
			var accepted = data.Builder.FundAccept(myKey, data.FundingTemplate);
			testOutputHelper.WriteLine("---Accept message---");
			testOutputHelper.WriteLine(JsonConvert.SerializeObject(accepted, TestnetSettings));
			testOutputHelper.WriteLine("--------------------");
			if (data.FundingOverride == null)
			{
				testOutputHelper.WriteLine("----Expected funding ----");
				testOutputHelper.WriteLine(data.Builder.GetFundingTransaction().ToHex());
				testOutputHelper.WriteLine("--------------------");
			}

			data.Builder.Finalize1(data.Sign);
			var unsigned = data.Builder.GetFundingPSBT();
			testOutputHelper.WriteLine("---Partially signed funding PSBT---");
			testOutputHelper.WriteLine(unsigned.ToBase64());
			testOutputHelper.WriteLine("--------------------");

			if (data.OracleAttestation != null)
			{
				var outcomeSigned = data.Builder.Execute(myKey, data.OracleAttestation);

				testOutputHelper.WriteLine("---Signed CET---");
				testOutputHelper.WriteLine(outcomeSigned.CET.ToHex());
				testOutputHelper.WriteLine("--------------------");
			}
		}

		[Fact]
		public void testAdaptorVerify()
		{

			byte[]
			msg = toByteArray("024BDD11F2144E825DB05759BDD9041367A420FAD14B665FD08AF5B42056E5E2");
			byte[]
			adaptorSig = toByteArray("00CBE0859638C3600EA1872ED7A55B8182A251969F59D7D2DA6BD4AFEDF25F5021A49956234CBBBBEDE8CA72E0113319C84921BF1224897A6ABD89DC96B9C5B208");
			byte[]
			adaptorProof = toByteArray("00B02472BE1BA09F5675488E841A10878B38C798CA63EFF3650C8E311E3E2EBE2E3B6FEE5654580A91CC5149A71BF25BCBEAE63DEA3AC5AD157A0AB7373C3011D0FC2592A07F719C5FC1323F935569ECD010DB62F045E965CC1D564EB42CCE8D6D");
			byte[]
			adaptor = toByteArray("038D48057FC4CE150482114D43201B333BF3706F3CD527E8767CEB4B443AB5D349");
			byte[]
			pubkey = toByteArray("03490CEC9A53CD8F2F664AEA61922F26EE920C42D2489778BB7C9D9ECE44D149A7");

			bool result = adaptorVerify(adaptorSig, pubkey, msg, adaptor, adaptorProof);

			assertEquals(result, true, "testAdaptorVeirfy");
		}

		[Fact]
		public void testAdaptorAdapt()
		{
			byte[] secret = toByteArray("475697A71A74FF3F2A8F150534E9B67D4B0B6561FAB86FCAA51F8C9D6C9DB8C6");
			byte[] adaptorSig = toByteArray("01099C91AA1FE7F25C41085C1D3C9E73FE04A9D24DAC3F9C2172D6198628E57F47BB90E2AD6630900B69F55674C8AD74A419E6CE113C10A21A79345A6E47BC74C1");

			byte[] resultArr = adaptorAdapt(secret, adaptorSig);

			String expectedSig = "30440220099C91AA1FE7F25C41085C1D3C9E73FE04A9D24DAC3F9C2172D6198628E57F4702204D13456E98D8989043FD4674302CE90C432E2F8BB0269F02C72AAFEC60B72DE1";
			String sigString = toHex(resultArr);
			assertEquals(sigString, expectedSig, "testAdaptorAdapt");
		}

		[Fact]
		public void testAdaptorExtractSecret()
		{
			byte[] sig = toByteArray("30440220099C91AA1FE7F25C41085C1D3C9E73FE04A9D24DAC3F9C2172D6198628E57F4702204D13456E98D8989043FD4674302CE90C432E2F8BB0269F02C72AAFEC60B72DE1");
			byte[] adaptorSig = toByteArray("01099C91AA1FE7F25C41085C1D3C9E73FE04A9D24DAC3F9C2172D6198628E57F47BB90E2AD6630900B69F55674C8AD74A419E6CE113C10A21A79345A6E47BC74C1");
			byte[] adaptor = toByteArray("038D48057FC4CE150482114D43201B333BF3706F3CD527E8767CEB4B443AB5D349");

			byte[] resultArr = adaptorExtractSecret(sig, adaptorSig, adaptor);

			String expectedSecret = "475697A71A74FF3F2A8F150534E9B67D4B0B6561FAB86FCAA51F8C9D6C9DB8C6";
			String sigString = toHex(resultArr);
			assertEquals(sigString, expectedSecret, "testAdaptorExtractSecret");
		}

		[Fact]
		public void testSchnorrSign()
		{

			byte[] data = toByteArray("E48441762FB75010B2AA31A512B62B4148AA3FB08EB0765D76B252559064A614");
			byte[] secKey = toByteArray("688C77BC2D5AAFF5491CF309D4753B732135470D05B7B2CD21ADD0744FE97BEF");
			byte[] auxRand = toByteArray("02CCE08E913F22A36C5648D6405A2C7C50106E7AA2F1649E381C7F09D16B80AB");

			byte[] sigArr = schnorrSign(data, secKey, auxRand);
			String sigStr = toHex(sigArr);
			String expectedSig = "6470FD1303DDA4FDA717B9837153C24A6EAB377183FC438F939E0ED2B620E9EE5077C4A8B8DCA28963D772A94F5F0DDF598E1C47C137F91933274C7C3EDADCE8";
			assertEquals(sigStr, expectedSig, "testSchnorrSign");
		}

		private byte[] schnorrSign(byte[] data, byte[] secKey, byte[] auxRand)
		{
			Assert.True(Context.Instance.TryCreateECPrivKey(secKey, out var key));
			Assert.True(key.TrySignBIP140(data, new BIP340NonceFunction(auxRand), out var sig));
			var buf = new byte[64];
			sig.WriteToSpan(buf);
			return buf;
		}

		[Fact]
		public void testSchnorrComputeSigPoint()
		{

			byte[] data = toByteArray("E48441762FB75010B2AA31A512B62B4148AA3FB08EB0765D76B252559064A614");
			byte[] nonce = toByteArray("F14D7E54FF58C5D019CE9986BE4A0E8B7D643BD08EF2CDF1099E1A457865B547");
			byte[] pubKey = toByteArray("B33CC9EDC096D0A83416964BD3C6247B8FECD256E4EFA7870D2C854BDEB33390");

			byte[] pointArr = schnorrComputeSigPoint(data, nonce, pubKey, true);
			String pointStr = toHex(pointArr);
			String expectedPoint = "03735ACF82EEF9DA1540EFB07A68251D5476DABB11AC77054924ECCBB4121885E8";
			assertEquals(pointStr, expectedPoint, "testSchnorrComputeSigPoint");
		}
		[Fact]
		public void testSchnorrComputeSigPoint2()
		{
			byte[] data = toByteArray("FB84860B10A497DEDDC3EFB45D20786ED72D27CFCF54A09A0E1C04DCEF4882A1");
			byte[] nonce = toByteArray("F67F8F41718C86F05EB95FAB308F5ED788A2A963124299154648F97124CAA579");
			byte[] pubKey = toByteArray("E4D36E995FF4BBA4DA2B60AD907D61D36E120D6F7314A3C2A20C6E27A5CD850F");

			byte[] pointArr = schnorrComputeSigPoint(data, nonce, pubKey, true);
			String pointStr = toHex(pointArr);
			String expectedPoint = "03C88D853DEA7F3E9C33027E99680446E4FB2ABF87704475522C8793CD1B03684B";
			assertEquals(pointStr, expectedPoint, "testSchnorrComputeSigPoint");
		}

		private byte[] schnorrComputeSigPoint(byte[] data, byte[] nonce, byte[] pubKey, bool compressed)
		{
			Assert.True(ECXOnlyPubKey.TryCreate(pubKey, Context.Instance, out var pk));
			Assert.True(SchnorrNonce.TryCreate(nonce, out var n));
			Assert.True(new OracleInfo(pk, n).TryComputeSigpoint(new DiscreteOutcome(data), out var sigpoint));
			return sigpoint.ToBytes(compressed);
		}

		[Fact]
		public void testSchnorrVerify()
		{

			byte[] sig = toByteArray("6470FD1303DDA4FDA717B9837153C24A6EAB377183FC438F939E0ED2B620E9EE5077C4A8B8DCA28963D772A94F5F0DDF598E1C47C137F91933274C7C3EDADCE8");
			byte[] data = toByteArray("E48441762FB75010B2AA31A512B62B4148AA3FB08EB0765D76B252559064A614");
			byte[] pubx = toByteArray("B33CC9EDC096D0A83416964BD3C6247B8FECD256E4EFA7870D2C854BDEB33390");

			var result = schnorrVerify(sig, data, pubx);

			assertEquals(result, true, "testSchnorrVerify");
		}

		private bool schnorrVerify(byte[] sig, byte[] data, byte[] pubx)
		{
			Assert.True(NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(sig, out var o));
			Assert.True(ECXOnlyPubKey.TryCreate(pubx, Context.Instance, out var pub));
			return pub.SigVerifyBIP340(o, data);
		}

		[Fact]
		public void testSchnorrSignWithNonce()
		{

			byte[] data = toByteArray("E48441762FB75010B2AA31A512B62B4148AA3FB08EB0765D76B252559064A614");
			byte[] secKey = toByteArray("688C77BC2D5AAFF5491CF309D4753B732135470D05B7B2CD21ADD0744FE97BEF");
			byte[] nonce = toByteArray("8C8CA771D3C25EB38DE7401818EEDA281AC5446F5C1396148F8D9D67592440FE");

			byte[] sigArr = schnorrSignWithNonce(data, secKey, nonce);
			String sigStr = toHex(sigArr);
			String expectedSig = "5DA618C1936EC728E5CCFF29207F1680DCF4146370BDCFAB0039951B91E3637A958E91D68537D1F6F19687CEC1FD5DB1D83DA56EF3ADE1F3C611BABD7D08AF42";
			assertEquals(sigStr, expectedSig, "testSchnorrSignWithNonce");
		}

		private byte[] schnorrSignWithNonce(byte[] data, byte[] secKey, byte[] nonce)
		{
			Assert.True(Context.Instance.TryCreateECPrivKey(secKey, out var key));
			Assert.True(key.TrySignBIP140(data, new PrecomputedNonceFunctionHardened(nonce), out var sig));
			var buf = new byte[64];
			sig.WriteToSpan(buf);
			return buf;
		}

		private byte[] adaptorAdapt(byte[] secret, byte[] adaptorSig)
		{
			Assert.True(SecpECDSAAdaptorSignature.TryCreate(adaptorSig, out var adaptorSigObj));
			var privKey = Context.Instance.CreateECPrivKey(secret);
			return adaptorSigObj.AdaptECDSA(privKey).ToDER();
		}

		private byte[] adaptorExtractSecret(byte[] sig, byte[] adaptorSig, byte[] adaptor)
		{
			Assert.True(SecpECDSAAdaptorSignature.TryCreate(adaptorSig, out var adaptorSigObj));
			Assert.True(SecpECDSASignature.TryCreateFromDer(sig, out var sigObj));
			Assert.True(Context.Instance.TryCreatePubKey(adaptor, out var pubkey));
			Assert.True(adaptorSigObj.TryExtractSecret(sigObj, pubkey, out var secret));
			var result = new byte[32];
			secret.WriteToSpan(result);
			return result;
		}
		private bool adaptorVerify(byte[] adaptorSig, byte[] pubkey, byte[] msg, byte[] adaptor, byte[] adaptorProof)
		{
			Assert.True(SecpECDSAAdaptorSignature.TryCreate(adaptorSig, out var adaptorSigObj));
			Assert.True(Context.Instance.TryCreatePubKey(pubkey, out var pubkeyObj));
			Assert.True(Context.Instance.TryCreatePubKey(adaptor, out var adaptorObj));
			Assert.True(SecpECDSAAdaptorProof.TryCreate(adaptorProof, out var adaptorProofObj));
			return pubkeyObj.SigVerify(adaptorSigObj, adaptorProofObj, msg, adaptorObj);
		}
		private byte[] adaptorSign(byte[] seckey, byte[] adaptor, byte[] msg)
		{
			var seckeyObj = Context.Instance.CreateECPrivKey(seckey);
			Assert.True(Context.Instance.TryCreatePubKey(adaptor, out var adaptorObj));
			Assert.True(seckeyObj.TrySignAdaptor(msg, adaptorObj, out var sig, out var proof));
			var output = new byte[65 + 97];
			sig.WriteToSpan(output);
			proof.WriteToSpan(output.AsSpan().Slice(65));
			return output;
		}

		byte[] toByteArray(string hex)
		{
			return Encoders.Hex.DecodeData(hex.ToLowerInvariant());
		}
	}
}
