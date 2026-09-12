using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BDVM.Domain;

public interface IRuntimeStatePayloadProvider
{
    string Provide(string checkpointId, string? persistedPayload);
}

public sealed class AcquisitionRuntimeStateProvider : IRuntimeStatePayloadProvider
{
    private readonly object gate = new object();
    private VehicleAcquisitionSnapshot? current;

    public VehicleAcquisitionSnapshot? Current
    {
        get { lock (gate) return current; }
    }

    public string? LocalPlayerId
    {
        get
        {
            lock (gate)
                return current == null ? null : "local-" + current.CheckpointId.Substring(0, Math.Min(16, current.CheckpointId.Length));
        }
    }

    public string Provide(string checkpointId, string? persistedPayload)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
            throw new InvalidDataException("A real checkpoint ID is required before composing runtime state.");

        lock (gate)
        {
            if (current == null)
                current = string.IsNullOrWhiteSpace(persistedPayload)
                    ? Empty(checkpointId)
                    : VehicleAcquisitionPersistence.Deserialize(persistedPayload!, checkpointId);
            else if (!string.Equals(current.CheckpointId, checkpointId, StringComparison.Ordinal))
                throw new InvalidDataException("The in-memory BDVM state belongs to another career or branch.");

            return VehicleAcquisitionPersistence.Serialize(current);
        }
    }

    public PlayerEconomicState EnsureLocalPlayer(long initialPersonalBalance = 0)
    {
        lock (gate)
        {
            if (current == null) throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            var playerId = LocalPlayerId!;
            var engine = new CompanyEconomyEngine(current.Economy);
            var existed = current.Economy.Players.Any(x => x.PlayerId == playerId);
            engine.EnsurePlayer(playerId, initialPersonalBalance);
            if (!existed)
                current.Economy.History.Add(new EconomicHistoryRecord
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Kind = "wallet-migration",
                    ActorIds = new List<string> { playerId },
                    Fingerprint = "host-keeps-legacy-balance-v1:" + initialPersonalBalance
                });
            return current.Economy.Players.Single(x => x.PlayerId == playerId);
        }
    }

    public PlayerEconomicState EnsurePersistentPlayer(string playerId, long initialPersonalBalance = 0)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("A persistent player ID is required.", nameof(playerId));
        lock (gate)
        {
            if (current == null) throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            var engine = new CompanyEconomyEngine(current.Economy);
            engine.EnsurePlayer(playerId, initialPersonalBalance);
            return current.Economy.Players.Single(x => x.PlayerId == playerId);
        }
    }

    public CommandRecord CreateCompanyFor(string commandId, string playerId, string name)
    {
        lock (gate)
        {
            EnsurePersistentPlayer(playerId);
            return new CompanyEconomyEngine(current!.Economy).CreateCompany(new EconomyCommand { CommandId = commandId, RequesterId = playerId }, name);
        }
    }

    public CommandRecord SynchronizeLocalWallet(string commandId, long authoritativeBalance, string source)
        => SynchronizeWalletFor(commandId, EnsureLocalPlayer(authoritativeBalance).PlayerId, authoritativeBalance, source);

    public CommandRecord SynchronizeWalletFor(string commandId, string playerId, long authoritativeBalance, string source)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId, authoritativeBalance);
            var wallet = current!.Economy.Wallets.Single(x => x.Account.Kind == AccountKind.Player && x.Account.OwnerId == player.PlayerId);
            if (wallet.Balance == authoritativeBalance)
                return new CommandRecord { CommandId = commandId, RequesterId = player.PlayerId, State = CommandState.Succeeded, ResultCode = "wallet-current" };
            return new CompanyEconomyEngine(current.Economy).SynchronizePersonalWallet(
                new EconomyCommand { CommandId = commandId, RequesterId = player.PlayerId }, authoritativeBalance, source);
        }
    }

    public ExternalWalletMirrorPlan PlanExternalWalletMirror(string playerId, long externalBalance, string operationId)
    {
        lock (gate)
        {
            EnsurePersistentPlayer(playerId, externalBalance);
            return new ExternalWalletMirrorEngine(current!.Economy).Plan(playerId, externalBalance, operationId);
        }
    }

    public ExternalWalletMirrorRecord CompleteExternalWalletMirror(string playerId, long synchronizedBalance, string operationId)
    {
        lock (gate)
        {
            EnsurePersistentPlayer(playerId, synchronizedBalance);
            return new ExternalWalletMirrorEngine(current!.Economy).Complete(playerId, synchronizedBalance, operationId);
        }
    }

    public CommandRecord CreateCompany(string commandId, string name)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            var engine = new CompanyEconomyEngine(current!.Economy);
            return engine.CreateCompany(new EconomyCommand { CommandId = commandId, RequesterId = player.PlayerId }, name);
        }
    }

    public CommandRecord ApplyToCompanyFor(string commandId, string playerId, string companyId, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var player = EnsurePersistentPlayer(playerId); var company = current!.Economy.Companies.Single(x => x.CompanyId == companyId);
            return new CompanyEconomyEngine(current.Economy).SubmitApplication(new EconomyCommand
            {
                CommandId = commandId, RequesterId = player.PlayerId, CompanyId = companyId,
                ExpectedVersions = new Dictionary<string, long> { ["player:" + player.PlayerId] = player.Version, ["company:" + company.CompanyId] = company.Version }
            }, companyId);
        }
    }

    public CommandRecord InvitePlayerFor(string commandId, string actorId, string companyId, string targetPlayerId, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var actor = EnsurePersistentPlayer(actorId); var target = EnsurePersistentPlayer(targetPlayerId); var company = current!.Economy.Companies.Single(x => x.CompanyId == companyId);
            return new CompanyEconomyEngine(current.Economy).SendInvitation(new EconomyCommand
            {
                CommandId = commandId, RequesterId = actor.PlayerId, CompanyId = companyId,
                ExpectedVersions = new Dictionary<string, long> { ["company:" + company.CompanyId] = company.Version, ["player:" + target.PlayerId] = target.Version }
            }, target.PlayerId);
        }
    }

    public CommandRecord DecideApplicationFor(string commandId, string actorId, string requestId, bool accept, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var actor = EnsurePersistentPlayer(actorId); var request = current!.Economy.MembershipRequests.Single(x => x.RequestId == requestId); var company = current.Economy.Companies.Single(x => x.CompanyId == request.CompanyId); var target = current.Economy.Players.Single(x => x.PlayerId == request.PlayerId);
            return new CompanyEconomyEngine(current.Economy).DecideApplication(new EconomyCommand
            {
                CommandId = commandId, RequesterId = actor.PlayerId, CompanyId = company.CompanyId,
                ExpectedVersions = GovernanceVersions(company, target, request)
            }, requestId, accept);
        }
    }

    public CommandRecord RespondToInvitationFor(string commandId, string playerId, string requestId, bool accept, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var player = EnsurePersistentPlayer(playerId); var request = current!.Economy.MembershipRequests.Single(x => x.RequestId == requestId); var company = current.Economy.Companies.Single(x => x.CompanyId == request.CompanyId);
            return new CompanyEconomyEngine(current.Economy).RespondToInvitation(new EconomyCommand
            {
                CommandId = commandId, RequesterId = player.PlayerId, CompanyId = company.CompanyId,
                ExpectedVersions = GovernanceVersions(company, player, request)
            }, requestId, accept);
        }
    }

    public CommandRecord SetMembershipPolicyFor(string commandId, string actorId, string companyId, MembershipPolicy policy, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var actor = EnsurePersistentPlayer(actorId); var company = current!.Economy.Companies.Single(x => x.CompanyId == companyId);
            return new CompanyEconomyEngine(current.Economy).ChangeMembershipPolicy(new EconomyCommand { CommandId = commandId, RequesterId = actor.PlayerId, CompanyId = companyId, ExpectedVersions = new Dictionary<string, long> { ["company:" + companyId] = company.Version } }, policy);
        }
    }

    public CommandRecord SetPermissionFor(string commandId, string actorId, string companyId, string memberId, CompanyPermission permission, bool enabled, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var actor = EnsurePersistentPlayer(actorId); EnsurePersistentPlayer(memberId); var company = current!.Economy.Companies.Single(x => x.CompanyId == companyId);
            return new CompanyEconomyEngine(current.Economy).ChangeDelegation(new EconomyCommand { CommandId = commandId, RequesterId = actor.PlayerId, CompanyId = companyId, ExpectedVersions = new Dictionary<string, long> { ["company:" + companyId] = company.Version } }, memberId, permission, enabled);
        }
    }

    public CommandRecord LeaveCompanyFor(string commandId, string playerId, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var player = EnsurePersistentPlayer(playerId);
            var expectedVersions = new Dictionary<string, long> { ["player:" + player.PlayerId] = player.Version };
            string? companyId = null;
            if (player.CompanyId != null)
            {
                var company = current!.Economy.Companies.Single(x => x.CompanyId == player.CompanyId);
                companyId = company.CompanyId;
                expectedVersions["company:" + company.CompanyId] = company.Version;
            }
            return new CompanyEconomyEngine(current!.Economy).Leave(new EconomyCommand { CommandId = commandId, RequesterId = player.PlayerId, CompanyId = companyId, ExpectedVersions = expectedVersions });
        }
    }

    public CommandRecord TransferLeadershipFor(string commandId, string actorId, string companyId, string newLeaderId, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var actor = EnsurePersistentPlayer(actorId); EnsurePersistentPlayer(newLeaderId); var company = current!.Economy.Companies.Single(x => x.CompanyId == companyId);
            return new CompanyEconomyEngine(current.Economy).TransferLeadershipCommand(new EconomyCommand { CommandId = commandId, RequesterId = actor.PlayerId, CompanyId = companyId, ExpectedVersions = new Dictionary<string, long> { ["company:" + companyId] = company.Version } }, newLeaderId);
        }
    }

    public CompanyLiquidationRecord DissolveCompanyFor(string commandId, string actorId, string companyId, long debts, long penalties,
        INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world, ICompanyContractCancellationPort contracts,
        ICompanyLiquidationCheckpointPort? checkpoint = null)
    {
        lock (gate)
        {
            RequireHost(authority); EnsurePersistentPlayer(actorId);
            return new CompanyLiquidationEngine(current!, authority, releaseGuard, world, contracts, checkpoint).Dissolve(commandId, actorId, companyId, debts, penalties);
        }
    }

    public IReadOnlyList<CompanyLiquidationRecord> ReconcilePendingCompanyLiquidations(INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard,
        IExistingVehicleOwnershipAdapter world, ICompanyContractCancellationPort contracts, ICompanyLiquidationCheckpointPort? checkpoint = null)
    {
        lock (gate)
        {
            RequireHost(authority);
            var engine = new CompanyLiquidationEngine(current!, authority, releaseGuard, world, contracts, checkpoint);
            return current!.CompanyLiquidations.Where(x => x.State == CompanyLiquidationState.ReconcileRequired).Select(x => x.CommandId).ToArray().Select(engine.Reconcile).ToArray();
        }
    }

    public OperatingCostRecord BeginLocalOperatingCost(string sessionId, string assetId, MaintenanceAction action, bool companyPayer,
        long maximumAuthorizedCost, long vanillaBalanceBefore, decimal conditionBefore, string? tripId, INetworkRoleDetector authority)
        => BeginOperatingCostFor(sessionId, EnsureLocalPlayer().PlayerId, assetId, action, companyPayer, maximumAuthorizedCost, vanillaBalanceBefore, conditionBefore, tripId, authority);

    public OperatingCostRecord BeginOperatingCostFor(string sessionId, string playerId, string assetId, MaintenanceAction action, bool companyPayer,
        long maximumAuthorizedCost, long authoritativeBalanceBefore, decimal conditionBefore, string? tripId, INetworkRoleDetector authority,
        OperatingCostSettlementMode settlementMode = OperatingCostSettlementMode.PersonalExternalWallet)
    {
        lock (gate)
        {
            RequireHost(authority); var player = EnsurePersistentPlayer(playerId);
            var payer = companyPayer ? AccountRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company payer requires company membership.")) : AccountRef.Player(player.PlayerId);
            return new OperatingCostEngine(current!, authority).Begin(new ManualMaintenanceRequest
            {
                CommandId = sessionId, RequesterId = player.PlayerId, AssetId = assetId, Action = action, Payer = payer,
                MaximumAuthorizedCost = maximumAuthorizedCost, ExplicitUserConfirmation = true
            }, authoritativeBalanceBefore, conditionBefore, tripId, settlementMode);
        }
    }

    public OperatingCostRecord CompleteLocalOperatingCost(string sessionId, long vanillaBalanceAfter, decimal conditionAfter, INetworkRoleDetector authority)
        => CompleteOperatingCostFor(EnsureLocalPlayer().PlayerId, sessionId, vanillaBalanceAfter, conditionAfter, authority);

    public OperatingCostRecord CompleteOperatingCostFor(string playerId, string sessionId, long authoritativeBalanceAfter, decimal conditionAfter, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); RequireOperatingCostRequester(playerId, sessionId); return new OperatingCostEngine(current!, authority).Complete(sessionId, authoritativeBalanceAfter, conditionAfter); } }

    public OperatingCostRecord CancelLocalOperatingCost(string sessionId, long observedVanillaBalance, INetworkRoleDetector authority)
        => CancelOperatingCostFor(EnsureLocalPlayer().PlayerId, sessionId, observedVanillaBalance, authority);

    public OperatingCostRecord CancelOperatingCostFor(string playerId, string sessionId, long observedAuthoritativeBalance, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); RequireOperatingCostRequester(playerId, sessionId); return new OperatingCostEngine(current!, authority).Cancel(sessionId, observedAuthoritativeBalance); } }

    public OperatingCostRecord MarkLocalOperatingCostExternalSettlement(string sessionId, long observedVanillaBalance, INetworkRoleDetector authority)
        => MarkOperatingCostExternalSettlementFor(EnsureLocalPlayer().PlayerId, sessionId, observedVanillaBalance, authority);

    public OperatingCostRecord MarkOperatingCostExternalSettlementFor(string playerId, string sessionId, long observedAuthoritativeBalance, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); RequireOperatingCostRequester(playerId, sessionId); return new OperatingCostEngine(current!, authority).MarkExternalSettlement(sessionId, observedAuthoritativeBalance); } }

    private void RequireOperatingCostRequester(string playerId, string sessionId)
    {
        EnsurePersistentPlayer(playerId);
        var record = current!.OperatingCosts.Single(value => value.SessionId == sessionId);
        if (!string.Equals(record.RequesterId, playerId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only the player who opened the operating-cost session may settle it.");
    }

    public CommandRecord TransferLocalCompany(string commandId, long amount, bool toCompany)
        => TransferCompanyFor(commandId, EnsureLocalPlayer().PlayerId, amount, toCompany);

    public CommandRecord TransferCompanyFor(string commandId, string playerId, long amount, bool toCompany)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId);
            if (string.IsNullOrWhiteSpace(player.CompanyId))
                throw new InvalidOperationException("The player does not belong to a company.");
            var playerAccount = AccountRef.Player(player.PlayerId);
            var companyAccount = AccountRef.Company(player.CompanyId!);
            var debit = toCompany ? playerAccount : companyAccount;
            var credit = toCompany ? companyAccount : playerAccount;
            var debitWallet = current!.Economy.Wallets.Single(x => x.Account.Key == debit.Key);
            var creditWallet = current.Economy.Wallets.Single(x => x.Account.Key == credit.Key);
            return new CompanyEconomyEngine(current.Economy).Transfer(
                new EconomyCommand
                {
                    CommandId = commandId,
                    RequesterId = player.PlayerId,
                    CompanyId = player.CompanyId,
                    ExpectedVersions = new Dictionary<string, long>
                    {
                        [debit.Key] = debitWallet.Version,
                        [credit.Key] = creditWallet.Version
                    }
                }, debit, credit, amount, toCompany ? LedgerEntryKind.Contribution : LedgerEntryKind.Withdrawal);
        }
    }

    public VehicleOffer PrepareVisibleVehicleOffer(VehicleInstanceRecord vehicle, long price)
    {
        if (vehicle == null) throw new ArgumentNullException(nameof(vehicle));
        if (vehicle.Resolution != ResolutionState.Resolved || string.IsNullOrWhiteSpace(vehicle.ExistingPersistentId) || string.IsNullOrWhiteSpace(vehicle.DefinitionId))
            throw new InvalidOperationException("Only one explicitly resolved non-traffic vehicle can become an offer.");
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
        lock (gate)
        {
            if (current == null) throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            var asset = EnsureVisibleAsset(vehicle);
            var offer = current.Offers.SingleOrDefault(x => x.AssetId == asset.AssetId && x.State == OfferState.Available);
            if (offer != null) return offer;
            offer = new VehicleOffer
            {
                OfferId = "runtime-offer-" + asset.AssetId,
                AssetId = asset.AssetId,
                Price = price,
                ReferenceValue = price,
                ReferenceSource = ReferenceValueSource.ConfiguredModel,
                ObservedCondition = 1m,
                AppliedRate = 1m
            };
            current.Offers.Add(offer);
            return offer;
        }
    }

    public MarketCatalogEntry ConfigureFiniteMarketDefinition(string definitionId, string categoryId, long basePrice, long transferFee,
        decimal minimumFactor, decimal maximumFactor, decimal buybackRate, string locationId, int stock)
    {
        lock (gate)
        {
            if (current == null) throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            if (string.IsNullOrWhiteSpace(definitionId) || string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(locationId)) throw new ArgumentException("Definition, category and location are required.");
            if (basePrice <= 0 || transferFee < 0 || minimumFactor <= 0m || maximumFactor < minimumFactor || buybackRate <= 0m || buybackRate >= 1m || stock < 0)
                throw new ArgumentOutOfRangeException(nameof(basePrice), "Market price, factors, margin, fee and stock must form a valid bounded catalog entry.");
            var existing = current.Market.Catalog.SingleOrDefault(x => x.DefinitionId == definitionId);
            if (existing == null)
            {
                existing = new MarketCatalogEntry { DefinitionId = definitionId, CategoryId = categoryId, BasePrice = basePrice, TransferFee = transferFee,
                    MinimumMarketFactor = minimumFactor, MaximumMarketFactor = maximumFactor, MinimumConditionFactor = 0.4m,
                    BuybackRate = buybackRate, QuoteDurationTicks = 100 };
                current.Market.Catalog.Add(existing);
            }
            else if (existing.CategoryId != categoryId || existing.BasePrice != basePrice || existing.TransferFee != transferFee ||
                existing.MinimumMarketFactor != minimumFactor || existing.MaximumMarketFactor != maximumFactor || existing.BuybackRate != buybackRate)
                throw new InvalidOperationException("A catalog definition is immutable once registered; create a future versioned definition instead.");
            var stockEntry = current.Market.Stock.SingleOrDefault(x => x.DefinitionId == definitionId && x.LocationId == locationId);
            if (stockEntry == null) current.Market.Stock.Add(new MarketStockEntry { DefinitionId = definitionId, LocationId = locationId, Available = stock, Capacity = stock });
            else if (stockEntry.Capacity != stock) throw new InvalidOperationException("A finite stock capacity cannot be silently rewritten.");
            FiniteMarketValidation.Validate(current.Market);
            return existing;
        }
    }

    public MarketListing PublishVisibleMarketListing(string listingId, VehicleInstanceRecord vehicle, string locationId, decimal condition,
        decimal marketFactor, INetworkRoleDetector authority, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            RequireHost(authority);
            var asset = EnsureVisibleAsset(vehicle);
            return new FiniteMarketEngine(current!, authority, world, new DisabledMarketDeliveryPort()).PublishExisting(listingId, asset.AssetId, locationId, condition, marketFactor);
        }
    }

    public MarketListing GenerateFiniteMarketOrder(string listingId, string definitionId, string locationId,
        INetworkRoleDetector authority, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { RequireHost(authority); return new FiniteMarketEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, world, new DisabledMarketDeliveryPort()).GenerateNewOrder(listingId, definitionId, locationId); }
    }

    public void AdvanceFiniteMarket(long tick, INetworkRoleDetector authority, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { RequireHost(authority); new FiniteMarketEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, world, new DisabledMarketDeliveryPort()).AdvanceTo(tick); }
    }

    public MarketPurchaseRecord PurchaseLocalMarket(string commandId, string listingId, bool forCompany, INetworkRoleDetector authority,
        IExistingVehicleOwnershipAdapter world, IMarketDeliveryPort delivery)
        => PurchaseMarketFor(commandId, EnsureLocalPlayer().PlayerId, listingId, forCompany, authority, world, delivery);

    public MarketPurchaseRecord PurchaseMarketFor(string commandId, string playerId, string listingId, bool forCompany, INetworkRoleDetector authority,
        IExistingVehicleOwnershipAdapter world, IMarketDeliveryPort delivery)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId); var listing = current!.Market.Listings.Single(x => x.ListingId == listingId);
            var payer = forCompany ? AccountRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company purchase requires membership.")) : AccountRef.Player(player.PlayerId);
            var buyer = forCompany ? AssetOwnerRef.Company(player.CompanyId!) : AssetOwnerRef.Player(player.PlayerId);
            var wallet = current.Economy.Wallets.Single(x => x.Account.Key == payer.Key);
            var company = forCompany ? current.Economy.Companies.Single(x => x.CompanyId == player.CompanyId) : null;
            return new FiniteMarketEngine(current, authority, world, delivery).Purchase(new MarketPurchaseCommand {
                CommandId = commandId, RequesterId = player.PlayerId, ListingId = listing.ListingId, Buyer = buyer, Payer = payer,
                ExpectedListingVersion = listing.Version, ExpectedWalletVersion = wallet.Version, ExpectedPlayerVersion = player.Version,
                ExpectedCompanyVersion = company?.Version });
        }
    }

    public VehicleResaleQuote PrepareLocalMarketBuybackQuote(string quoteId, string assetId, decimal condition, decimal marketFactor,
        long fuelValue, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            return new FiniteMarketEngine(current!, authority, world, new DisabledMarketDeliveryPort()).PrepareBuybackQuote(
                quoteId, player.PlayerId, assetId, condition, marketFactor, fuelValue, releaseGuard);
        }
    }

    public IReadOnlyList<MarketPurchaseRecord> ReconcilePendingMarketPurchases(INetworkRoleDetector authority,
        IExistingVehicleOwnershipAdapter world, IMarketDeliveryPort delivery)
    {
        lock (gate)
        {
            var engine = new FiniteMarketEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, world, delivery);
            return current.Market.Purchases.Where(x => x.State == MarketPurchaseState.ReconcileRequired).Select(x => x.CommandId).ToArray().Select(engine.Reconcile).ToArray();
        }
    }

    public InitialDeliveryGrant GrantLocalStarterBundle(string commandId, IReadOnlyList<string> definitionIds,
        INetworkRoleDetector authority)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            return GrantStarterBundleFor(commandId, player.PlayerId, definitionIds, authority);
        }
    }

    public InitialDeliveryGrant GrantStarterBundleFor(string commandId, string playerId, IReadOnlyList<string> definitionIds,
        INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); EnsurePersistentPlayer(playerId);
            return new InitialDeliveryEngine(current!, authority, new DisabledInitialDeliveryPort()).GrantStarterBundle(commandId, playerId, definitionIds);
        }
    }

    public InitialDeliveryGrant PlaceLocalInitialDelivery(string commandId, string grantId, string trackId,
        InitialDeliveryTargetKind targetKind, INetworkRoleDetector authority, IInitialDeliveryPort delivery,
        IInitialDeliveryCheckpointPort? checkpoint = null)
        => PlaceInitialDeliveryFor(commandId, EnsureLocalPlayer().PlayerId, grantId, trackId, targetKind, authority, delivery, checkpoint);

    public InitialDeliveryGrant PlaceInitialDeliveryFor(string commandId, string playerId, string grantId, string trackId,
        InitialDeliveryTargetKind targetKind, INetworkRoleDetector authority, IInitialDeliveryPort delivery,
        IInitialDeliveryCheckpointPort? checkpoint = null)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId);
            var grant = current!.InitialDeliveries.Single(x => x.GrantId == grantId);
            return new InitialDeliveryEngine(current, authority, delivery, checkpoint).Place(new InitialDeliveryCommand
            {
                CommandId = commandId,
                RequesterId = player.PlayerId,
                GrantId = grantId,
                TargetTrackId = trackId,
                TargetKind = targetKind,
                ExpectedGrantVersion = grant.Version
            });
        }
    }

    public IReadOnlyList<InitialDeliveryGrant> ReconcilePendingInitialDeliveries(INetworkRoleDetector authority,
        IInitialDeliveryPort delivery, IInitialDeliveryCheckpointPort? checkpoint = null)
    {
        lock (gate)
        {
            var engine = new InitialDeliveryEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, delivery, checkpoint);
            return current.InitialDeliveries.Where(x => x.State == InitialDeliveryState.PlacementPending || x.State == InitialDeliveryState.ReconcileRequired).Select(x => x.GrantId).ToArray().Select(engine.Reconcile).ToArray();
        }
    }

    public InitialDeliveryGrant ReconcileInitialDeliveryFor(string playerId, string grantId, INetworkRoleDetector authority,
        IInitialDeliveryPort delivery, IInitialDeliveryCheckpointPort? checkpoint = null)
    {
        lock (gate)
        {
            RequireHost(authority);
            var player = EnsurePersistentPlayer(playerId);
            var grant = current!.InitialDeliveries.Single(x => x.GrantId == grantId);
            var authorized = grant.AuthorizedOperator ?? grant.Owner;
            if (authorized.Kind == AssetOwnerKind.Player)
            {
                if (authorized.OwnerId != player.PlayerId) throw new UnauthorizedAccessException("Only the authorized player can reconcile this rolling stock delivery.");
            }
            else
            {
                if (authorized.Kind != AssetOwnerKind.Company || player.CompanyId != authorized.OwnerId) throw new UnauthorizedAccessException("Requester does not belong to the authorized company.");
                var company = current.Economy.Companies.Single(x => x.CompanyId == authorized.OwnerId);
                if (company.Liquidating || (company.LeaderId != player.PlayerId && (!company.DelegatedPermissions.TryGetValue(player.PlayerId, out var rights) || !rights.Contains(CompanyPermission.ManageFleet))))
                    throw new UnauthorizedAccessException("ManageFleet permission is required.");
            }
            return new InitialDeliveryEngine(current, authority, delivery, checkpoint).Reconcile(grantId);
        }
    }

    public LeaseContract CreateLocalLeaseOffer(string leaseId, IReadOnlyList<string> assetIds, long deposit, long initialFee, long rent,
        long interval, long duration, long? purchaseOption, decimal condition, long maximumDamageCharge, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { return new LeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, world).CreateOffer(leaseId, assetIds, deposit, initialFee, rent, interval, duration, purchaseOption, condition, maximumDamageCharge); }
    }

    public LeaseContract CreateLocalCatalogLeaseOffer(string leaseId, IReadOnlyList<string> definitionIds, long deposit, long initialFee, long rent,
        long interval, long duration, long? purchaseOption, decimal condition, long maximumDamageCharge, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { return new LeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, world).CreateCatalogOffer(leaseId, definitionIds, deposit, initialFee, rent, interval, duration, purchaseOption, condition, maximumDamageCharge); }
    }

    public LeaseContract CreateLocalCatalogListingLeaseOffer(string leaseId, IReadOnlyList<string> listingIds, long deposit, long initialFee, long rent,
        long interval, long duration, long? purchaseOption, decimal condition, long maximumDamageCharge, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { return new LeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, world).CreateCatalogListingOffer(leaseId, listingIds, deposit, initialFee, rent, interval, duration, purchaseOption, condition, maximumDamageCharge); }
    }

    public LeaseActionRecord AcceptLocalLease(string commandId, string leaseId, bool forCompany, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
        => AcceptLeaseFor(commandId, EnsureLocalPlayer().PlayerId, leaseId, forCompany, authority, releaseGuard, world);

    public LeaseActionRecord AcceptLeaseFor(string commandId, string playerId, string leaseId, bool forCompany, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId); var lease = current!.Leases.Single(x => x.LeaseId == leaseId);
            var payer = forCompany ? AccountRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company lease requires membership.")) : AccountRef.Player(player.PlayerId);
            var lessee = forCompany ? AssetOwnerRef.Company(player.CompanyId!) : AssetOwnerRef.Player(player.PlayerId); var wallet = current.Economy.Wallets.Single(x => x.Account.Key == payer.Key);
            var result = new LeaseEngine(current, authority, releaseGuard, world).Accept(commandId, player.PlayerId, leaseId, lessee, payer, lease.Version, wallet.Version);
            if (result.State == LeaseActionState.Succeeded && current.Assets.Assets.Where(asset => lease.AssetIds.Contains(asset.AssetId)).Any(asset => asset.GameLink.State != PersistentLinkState.Resolved))
                new InitialDeliveryEngine(current, authority, new DisabledInitialDeliveryPort()).GrantLeaseDelivery(commandId + ":delivery", leaseId);
            return result;
        }
    }

    public long AdvanceLocalLeaseClock(LeaseClockAdvance advance, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var state = current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            var tick = new LeaseEngine(state, authority, releaseGuard, world).Advance(advance);
            new OutboundLeaseEngine(state, authority, releaseGuard, new DeclaredOffSceneLeaseSimulationPort()).ProcessClock(advance.CommandId + ":outbound");
            return tick;
        }
    }

    public long AdvanceEconomicClockWithoutLeasing(LeaseClockAdvance advance, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            var state = current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out _)) throw new InvalidOperationException("Host authority is required.");
            if (string.IsNullOrWhiteSpace(advance.CommandId) || advance.ActiveGameplayTicks < 0 || advance.SleepTicks < 0 || advance.FastTravelTicks < 0)
                throw new ArgumentException("Invalid economic clock advance.");
            var fingerprint = string.Join("|", "economic-clock-no-leasing", advance.ActiveGameplayTicks, advance.SleepTicks, advance.FastTravelTicks, advance.SessionOpen, advance.Paused);
            var known = state.LeaseActions.SingleOrDefault(value => value.CommandId == advance.CommandId);
            if (known != null)
            {
                if (known.Fingerprint != fingerprint) throw new InvalidOperationException("Economic clock command ID payload conflict.");
                return state.LeaseClock.ActiveTick;
            }
            var delta = !advance.SessionOpen || advance.Paused ? 0 : checked(advance.ActiveGameplayTicks + advance.SleepTicks + advance.FastTravelTicks);
            state.LeaseClock.ActiveTick = checked(state.LeaseClock.ActiveTick + delta);
            state.LeaseClock.Version++;
            state.LeaseActions.Add(new LeaseActionRecord { CommandId = advance.CommandId, Fingerprint = fingerprint, LeaseId = "disabled", State = LeaseActionState.Succeeded, ResultCode = "economic-clock-advanced-without-leasing", Amount = delta });
            return state.LeaseClock.ActiveTick;
        }
    }

    public LeaseActionRecord ReturnLocalLease(string commandId, string leaseId, decimal condition, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
        => ReturnLeaseFor(commandId, EnsureLocalPlayer().PlayerId, leaseId, condition, authority, releaseGuard, world);

    public FleetCommandRecord ConfirmPhysicalFleetRemoval(string commandId, string persistentCarGuid, string source, INetworkRoleDetector authority)
    { lock (gate) { return new FleetManagementEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority).ConfirmPhysicalRemoval(commandId, persistentCarGuid, source); } }

    public LeaseActionRecord ReturnLeaseFor(string commandId, string playerId, string leaseId, decimal condition, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new LeaseEngine(current!, authority, releaseGuard, world).Return(commandId, player.PlayerId, leaseId, condition); } }

    public LeaseActionRecord PurchaseLocalLease(string commandId, string leaseId, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
        => PurchaseLeaseFor(commandId, EnsureLocalPlayer().PlayerId, leaseId, authority, releaseGuard, world);

    public LeaseActionRecord PurchaseLeaseFor(string commandId, string playerId, string leaseId, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new LeaseEngine(current!, authority, releaseGuard, world).ExercisePurchaseOption(commandId, player.PlayerId, leaseId); } }

    public IReadOnlyList<LeaseActionRecord> ReconcilePendingLeasePurchases(INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate) { var engine = new LeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, world); return current.LeaseActions.Where(x => x.State == LeaseActionState.ReconcileRequired).Select(x => x.CommandId).ToArray().Select(engine.ReconcilePurchase).ToArray(); }
    }

    public OutboundLeaseContract PublishLocalOutboundLease(string commandId, string contractId, IReadOnlyList<string> assetIds,
        long rent, long interval, long duration, long earlyRecallFee, decimal condition, string destination, string returnLocation,
        INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IOutboundLeaseSimulationPort simulation)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            return new OutboundLeaseEngine(current!, authority, releaseGuard, simulation).Publish(commandId, player.PlayerId, contractId, assetIds, rent, interval, duration, earlyRecallFee, condition, destination, returnLocation);
        }
    }

    public OutboundLeaseActionRecord ActivateLocalOutboundLease(string commandId, string contractId, INetworkRoleDetector authority,
        IAssetReleaseGuard releaseGuard, IOutboundLeaseSimulationPort simulation)
    { lock (gate) { var player = EnsureLocalPlayer(); return new OutboundLeaseEngine(current!, authority, releaseGuard, simulation).Activate(commandId, player.PlayerId, contractId); } }

    public OutboundLeaseActionRecord ReturnLocalOutboundLease(string commandId, string contractId, decimal condition, string returnLocation,
        INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IOutboundLeaseSimulationPort simulation)
    { lock (gate) return new OutboundLeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, simulation).Return(commandId, contractId, condition, returnLocation); }

    public OutboundLeaseActionRecord RecallLocalOutboundLease(string commandId, string contractId, decimal condition, string returnLocation,
        INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IOutboundLeaseSimulationPort simulation)
    { lock (gate) { var player = EnsureLocalPlayer(); return new OutboundLeaseEngine(current!, authority, releaseGuard, simulation).Recall(commandId, player.PlayerId, contractId, condition, returnLocation); } }

    public IReadOnlyList<OutboundLeaseActionRecord> ReconcilePendingOutboundLeases(INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IOutboundLeaseSimulationPort simulation)
    {
        lock (gate)
        {
            var engine = new OutboundLeaseEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, releaseGuard, simulation);
            return current.OutboundLeaseActions.Where(x => x.State == OutboundLeaseActionState.ReconcileRequired).Select(x => x.CommandId).ToArray().Select(engine.Reconcile).ToArray();
        }
    }

    public MissionAssignment ReserveLocalAssignment(string commandId, string assignmentId, string missionId, MissionAssignmentKind kind,
        IReadOnlyList<string> assetIds, bool forCompany, long maximumRevenue, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => ReserveAssignmentFor(commandId, EnsureLocalPlayer().PlayerId, assignmentId, missionId, kind, assetIds, forCompany, maximumRevenue, authority, completion);

    public MissionAssignment ReserveAssignmentFor(string commandId, string playerId, string assignmentId, string missionId, MissionAssignmentKind kind,
        IReadOnlyList<string> assetIds, bool forCompany, long maximumRevenue, INetworkRoleDetector authority, IMissionCompletionPort completion)
    {
        lock (gate) { var player = EnsurePersistentPlayer(playerId); var op = forCompany ? AssetOwnerRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company assignment requires membership.")) : AssetOwnerRef.Player(player.PlayerId); return new MissionAssignmentEngine(current!, authority, completion).Reserve(commandId, player.PlayerId, assignmentId, missionId, kind, assetIds, op, maximumRevenue); }
    }

    public MissionAssignment StartLocalAssignment(string commandId, string assignmentId, long vanillaBalance, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => StartAssignmentFor(commandId, EnsureLocalPlayer().PlayerId, assignmentId, vanillaBalance, authority, completion);

    public MissionAssignment StartAssignmentFor(string commandId, string playerId, string assignmentId, long authoritativeBalance, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new MissionAssignmentEngine(current!, authority, completion).Start(commandId, player.PlayerId, assignmentId, authoritativeBalance); } }

    public MissionAssignment CompleteLocalAssignment(string commandId, string assignmentId, long vanillaBalance, IReadOnlyList<string> arrivedAssetIds,
        INetworkRoleDetector authority, IMissionCompletionPort completion)
        => CompleteAssignmentFor(commandId, EnsureLocalPlayer().PlayerId, assignmentId, vanillaBalance, arrivedAssetIds, authority, completion);

    public MissionAssignment CompleteAssignmentFor(string commandId, string playerId, string assignmentId, long authoritativeBalance, IReadOnlyList<string> arrivedAssetIds,
        INetworkRoleDetector authority, IMissionCompletionPort completion, MissionSettlementMode settlementMode = MissionSettlementMode.ExternalWalletIncludesRevenue)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new MissionAssignmentEngine(current!, authority, completion).Complete(commandId, player.PlayerId, assignmentId, authoritativeBalance, arrivedAssetIds, settlementMode); } }

    public MissionAssignment CancelLocalAssignment(string commandId, string assignmentId, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => CancelAssignmentFor(commandId, EnsureLocalPlayer().PlayerId, assignmentId, authority, completion);

    public MissionAssignment CancelAssignmentFor(string commandId, string playerId, string assignmentId, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new MissionAssignmentEngine(current!, authority, completion).Cancel(commandId, player.PlayerId, assignmentId); } }

    public MissionAssignment MarkLocalMissionSettlement(string assignmentId, long vanillaBalance, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => MarkMissionSettlementFor(EnsureLocalPlayer().PlayerId, assignmentId, vanillaBalance, authority, completion);

    public MissionAssignment MarkMissionSettlementFor(string playerId, string assignmentId, long authoritativeBalance, INetworkRoleDetector authority, IMissionCompletionPort completion)
    {
        lock (gate)
        {
            EnsurePersistentPlayer(playerId);
            var assignment = current!.Assignments.Single(value => value.AssignmentId == assignmentId);
            if (!string.Equals(assignment.RequestedBy, playerId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Only the player who reserved the assignment may settle it.");
            return new MissionAssignmentEngine(current, authority, completion).MarkExternalSettlement(assignmentId, authoritativeBalance);
        }
    }

    public PassengerRouteDemand ConfigureLocalPassengerRoute(string commandId, string routeId, string origin, string destination, int initialDemand,
        int maximumDemand, int demandPerInterval, long desiredFrequency, long fare, long latePenalty, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) return new PassengerEconomyEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, completion).ConfigureRoute(commandId, routeId, origin, destination, initialDemand, maximumDemand, demandPerInterval, desiredFrequency, fare, latePenalty); }

    public PassengerRouteDemand RefreshLocalPassengerDemand(string commandId, string routeId, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) return new PassengerEconomyEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority, completion).RefreshDemand(commandId, routeId, current!.LeaseClock.ActiveTick); }

    public PassengerServiceContract ReserveLocalPassengerService(string commandId, string contractId, string routeId, string passengerJobId,
        IReadOnlyList<string> assetIds, bool forCompany, int capacity, long departureTick, long arrivalTick, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => ReservePassengerServiceFor(commandId, EnsureLocalPlayer().PlayerId, contractId, routeId, passengerJobId, assetIds, forCompany, capacity, departureTick, arrivalTick, authority, completion);

    public PassengerServiceContract ReservePassengerServiceFor(string commandId, string playerId, string contractId, string routeId, string passengerJobId,
        IReadOnlyList<string> assetIds, bool forCompany, int capacity, long departureTick, long arrivalTick, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); var op = forCompany ? AssetOwnerRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company passenger service requires membership.")) : AssetOwnerRef.Player(player.PlayerId); return new PassengerEconomyEngine(current!, authority, completion).OfferAndReserve(commandId, player.PlayerId, contractId, routeId, passengerJobId, assetIds, op, capacity, departureTick, arrivalTick); } }

    public PassengerServiceContract StartLocalPassengerService(string commandId, string contractId, long vanillaBalance, long actualDepartureTick, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => StartPassengerServiceFor(commandId, EnsureLocalPlayer().PlayerId, contractId, vanillaBalance, actualDepartureTick, authority, completion);

    public PassengerServiceContract StartPassengerServiceFor(string commandId, string playerId, string contractId, long authoritativeBalance, long actualDepartureTick, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new PassengerEconomyEngine(current!, authority, completion).Start(commandId, player.PlayerId, contractId, authoritativeBalance, actualDepartureTick); } }

    public PassengerServiceContract CompleteLocalPassengerService(string commandId, string contractId, long vanillaBalance, long actualArrivalTick,
        IReadOnlyList<string> arrivedAssetIds, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => CompletePassengerServiceFor(commandId, EnsureLocalPlayer().PlayerId, contractId, vanillaBalance, actualArrivalTick, arrivedAssetIds, authority, completion);

    public PassengerServiceContract CompletePassengerServiceFor(string commandId, string playerId, string contractId, long authoritativeBalance, long actualArrivalTick,
        IReadOnlyList<string> arrivedAssetIds, INetworkRoleDetector authority, IMissionCompletionPort completion, MissionSettlementMode settlementMode = MissionSettlementMode.ExternalWalletIncludesRevenue)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new PassengerEconomyEngine(current!, authority, completion).Complete(commandId, player.PlayerId, contractId, authoritativeBalance, actualArrivalTick, arrivedAssetIds, settlementMode); } }

    public PassengerServiceContract CancelLocalPassengerService(string commandId, string contractId, INetworkRoleDetector authority, IMissionCompletionPort completion)
        => CancelPassengerServiceFor(commandId, EnsureLocalPlayer().PlayerId, contractId, authority, completion);

    public PassengerServiceContract CancelPassengerServiceFor(string commandId, string playerId, string contractId, INetworkRoleDetector authority, IMissionCompletionPort completion)
    { lock (gate) { var player = EnsurePersistentPlayer(playerId); return new PassengerEconomyEngine(current!, authority, completion).Cancel(commandId, player.PlayerId, contractId); } }

    public DynamicMarketPolicy ConfigureLocalDynamicMarketPolicy(string commandId, string categoryId, decimal minimum, decimal maximum,
        decimal smoothing, decimal maximumStep, decimal supplyWeight, decimal demandWeight, decimal utilizationWeight, INetworkRoleDetector authority)
    { lock (gate) return new DynamicEconomyEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority).ConfigurePolicy(commandId, categoryId, minimum, maximum, smoothing, maximumStep, supplyWeight, demandWeight, utilizationWeight); }

    public IReadOnlyList<DynamicMarketMetric> RecalculateLocalDynamicEconomy(string commandId, long tick, INetworkRoleDetector authority)
    { lock (gate) return new DynamicEconomyEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority).Recalculate(commandId, tick); }

    public decimal LocalDynamicFactorForDefinition(string definitionId, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); return new DynamicEconomyEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority).FactorForDefinition(definitionId); } }

    private FleetAsset EnsureVisibleAsset(VehicleInstanceRecord vehicle)
    {
        if (vehicle == null || vehicle.Resolution != ResolutionState.Resolved || string.IsNullOrWhiteSpace(vehicle.ExistingPersistentId) || string.IsNullOrWhiteSpace(vehicle.DefinitionId))
            throw new InvalidOperationException("Only one explicitly resolved non-traffic vehicle can enter the market.");
        var asset = current!.Assets.Assets.SingleOrDefault(x => string.Equals(x.GameLink.Value, vehicle.ExistingPersistentId, StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            if (!current.Assets.Definitions.Any(x => x.DefinitionId == vehicle.DefinitionId)) current.Assets.Definitions.Add(new AssetDefinition { DefinitionId = vehicle.DefinitionId!, Origin = vehicle.Origin?.ProviderId ?? vehicle.Origin?.Kind ?? "runtime" });
            asset = FleetAsset.Create(vehicle.DefinitionId!, vehicle.ExistingPersistentId!); asset.GameLink.State = PersistentLinkState.Resolved;
            asset.GameLink.Detail = "Selected explicitly from the current authoritative visible inventory.";
            current.Assets.Assets.Add(asset); current.Ownership.Add(new AssetOwnership { AssetId = asset.AssetId, Owner = AssetOwnerRef.Merchant("runtime-market") });
        }
        FleetManagementEngine.EnsureAsset(current, asset.AssetId, vehicle.Type, vehicle.DefinitionId, vehicle.ExistingVisibleId);
        return asset;
    }

    public AcquisitionRecord AcquireLocal(string commandId, string offerId, bool forCompany, INetworkRoleDetector authority, IExistingVehicleOwnershipAdapter world, IAcquisitionCheckpointSink checkpoints)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            var offer = current!.Offers.Single(x => x.OfferId == offerId);
            var ownership = current.Ownership.Single(x => x.AssetId == offer.AssetId);
            var payer = forCompany ? AccountRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company acquisition requires membership.")) : AccountRef.Player(player.PlayerId);
            var buyer = forCompany ? AssetOwnerRef.Company(player.CompanyId!) : AssetOwnerRef.Player(player.PlayerId);
            var wallet = current.Economy.Wallets.Single(x => x.Account.Key == payer.Key);
            var expected = new Dictionary<string, long>
            {
                ["player:" + player.PlayerId] = player.Version,
                [payer.Key] = wallet.Version,
                ["offer:" + offer.OfferId] = offer.Version,
                ["asset:" + ownership.AssetId] = ownership.Version
            };
            if (forCompany)
                expected["company:" + player.CompanyId] = current.Economy.Companies.Single(x => x.CompanyId == player.CompanyId).Version;
            return new VehicleAcquisitionEngine(current, world, authority, checkpoints).Acquire(new AcquireVehicleCommand
            {
                CommandId = commandId,
                RequesterId = player.PlayerId,
                OfferId = offer.OfferId,
                AssetId = offer.AssetId,
                Buyer = buyer,
                Payer = payer,
                ExpectedVersions = expected
            });
        }
    }

    public FleetCommandRecord ManageLocalFleet(string commandId, string assetId, FleetCommandAction action, INetworkRoleDetector authority,
        FleetOperationalState? operationalState = null, AssetOwnerRef? target = null, string? displayName = null)
        => ManageFleetFor(commandId, EnsureLocalPlayer().PlayerId, assetId, action, authority, operationalState, target, displayName);

    public FleetCommandRecord ManageFleetFor(string commandId, string playerId, string assetId, FleetCommandAction action, INetworkRoleDetector authority,
        FleetOperationalState? operationalState = null, AssetOwnerRef? target = null, string? displayName = null)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId);
            var fleet = current!.Fleet.Single(x => x.AssetId == assetId);
            var ownership = current.Ownership.Single(x => x.AssetId == assetId);
            return new FleetManagementEngine(current, authority).Execute(new FleetCommand
            {
                CommandId = commandId,
                RequesterId = player.PlayerId,
                AssetId = assetId,
                Action = action,
                ExpectedFleetVersion = fleet.Version,
                ExpectedOwnershipVersion = ownership.Version,
                OperationalState = operationalState,
                Target = target,
                DisplayName = displayName
            });
        }
    }

    public VehicleResaleQuote PrepareLocalResaleQuote(string quoteId, string assetId, long proceeds, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
        => PrepareResaleQuoteFor(quoteId, EnsureLocalPlayer().PlayerId, assetId, proceeds, authority, releaseGuard, world);

    public VehicleResaleQuote PrepareResaleQuoteFor(string quoteId, string playerId, string assetId, long proceeds, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId);
            var acquisition = current!.Acquisitions.LastOrDefault(x => x.AssetId == assetId && x.State == AcquisitionState.Succeeded);
            if (acquisition == null) throw new InvalidOperationException("No frozen acquisition reference is available for this asset.");
            var reference = Math.Max(acquisition.ReferenceValue, acquisition.Price);
            if (proceeds > reference) throw new InvalidOperationException("Validation resale proceeds cannot exceed the frozen acquisition reference.");
            return new VehicleResaleEngine(current, authority, releaseGuard, world).PrepareQuote(quoteId, player.PlayerId, assetId, proceeds, reference, acquisition.ReferenceSource, acquisition.ObservedCondition, 0, 0);
        }
    }

    public AssetBundle CreateLocalBundle(string commandId, IReadOnlyList<string> assetIds, INetworkRoleDetector authority)
        => CreateBundleFor(commandId, EnsureLocalPlayer().PlayerId, assetIds, authority);

    public AssetBundle CreateBundleFor(string commandId, string playerId, IReadOnlyList<string> assetIds, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out _)) throw new InvalidOperationException("Host authority is required.");
            var player = EnsurePersistentPlayer(playerId);
            var ids = (assetIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (ids.Count < 2) throw new InvalidOperationException("Select at least two owned assets for a bundle.");
            var fingerprint = string.Join(",", ids);
            var knownHistory = current!.Economy.History.SingleOrDefault(x => x.EventId == commandId && x.Kind == "asset-bundle-created");
            if (knownHistory != null)
            {
                if (knownHistory.Fingerprint != fingerprint) throw new InvalidOperationException("A bundle command ID cannot be reused with another selection.");
                return current.Assets.Bundles.Single(x => x.BundleId == knownHistory.CompanyId);
            }
            if (current.Assets.Bundles.Any(x => x.ComponentAssetIds.Any(ids.Contains))) throw new InvalidOperationException("A selected asset already belongs to a bundle.");
            var owners = ids.Select(id => current.Ownership.Single(x => x.AssetId == id).Owner).ToArray();
            if (owners.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != 1) throw new InvalidOperationException("Every bundle component must have the same owner.");
            var owner = owners[0];
            if (owner.Kind == AssetOwnerKind.Player && owner.OwnerId != player.PlayerId) throw new InvalidOperationException("The player does not own every selected asset.");
            if (owner.Kind == AssetOwnerKind.Company)
            {
                var company = current.Economy.Companies.SingleOrDefault(x => x.CompanyId == owner.OwnerId);
                if (company == null || player.CompanyId != company.CompanyId || (company.LeaderId != player.PlayerId && (!company.DelegatedPermissions.TryGetValue(player.PlayerId, out var rights) || !rights.Contains(CompanyPermission.ManageFleet))))
                    throw new InvalidOperationException("ManageFleet permission is required for a company bundle.");
            }
            else if (owner.Kind != AssetOwnerKind.Player) throw new InvalidOperationException("Merchant assets cannot be bundled by a player.");
            foreach (var id in ids) if (!current.Fleet.Any(x => x.AssetId == id)) throw new InvalidOperationException("Every bundle component must be in the fleet registry.");
            var bundle = new AssetBundle { BundleId = Guid.NewGuid().ToString("N"), ComponentAssetIds = ids };
            current.Assets.Bundles.Add(bundle);
            current.Economy.History.Add(new EconomicHistoryRecord { EventId = commandId, Kind = "asset-bundle-created", CompanyId = bundle.BundleId, ActorIds = new List<string> { player.PlayerId }, Fingerprint = fingerprint });
            return bundle;
        }
    }

    public VehicleResaleQuote PrepareLocalBundleResaleQuote(string quoteId, string bundleId, long proceeds, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var player = EnsureLocalPlayer();
            var bundle = current!.Assets.Bundles.Single(x => x.BundleId == bundleId);
            var acquisitions = bundle.ComponentAssetIds.Select(id => current.Acquisitions.LastOrDefault(x => x.AssetId == id && x.State == AcquisitionState.Succeeded) ?? throw new InvalidOperationException("Every bundle component requires a frozen acquisition reference.")).ToArray();
            var reference = acquisitions.Sum(x => Math.Max(x.ReferenceValue, x.Price));
            if (proceeds > reference) throw new InvalidOperationException("Validation resale proceeds cannot exceed the frozen bundle acquisition reference.");
            var condition = acquisitions.Length == 0 ? 1m : acquisitions.Average(x => x.ObservedCondition);
            var source = acquisitions.All(x => x.ReferenceSource == ReferenceValueSource.DynamicMarket) ? ReferenceValueSource.DynamicMarket : ReferenceValueSource.ConfiguredModel;
            return new VehicleResaleEngine(current, authority, releaseGuard, world).PrepareBundleQuote(quoteId, player.PlayerId, bundleId, proceeds, reference, source, condition, 0, 0);
        }
    }

    public VehicleResaleRecord SellLocalVehicle(string commandId, string quoteId, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
        => SellVehicleFor(commandId, EnsureLocalPlayer().PlayerId, quoteId, authority, releaseGuard, world);

    public VehicleResaleRecord SellVehicleFor(string commandId, string playerId, string quoteId, INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            var player = EnsurePersistentPlayer(playerId);
            var quote = current!.ResaleQuotes.Single(x => x.QuoteId == quoteId);
            var fleet = current.Fleet.Single(x => x.AssetId == quote.AssetId);
            var ownership = current.Ownership.Single(x => x.AssetId == quote.AssetId);
            var wallet = current.Economy.Wallets.Single(x => x.Account.Key == quote.Payee.Key);
            var fleetVersions = quote.AssetIds.ToDictionary(id => id, id => current.Fleet.Single(x => x.AssetId == id).Version, StringComparer.Ordinal);
            var ownershipVersions = quote.AssetIds.ToDictionary(id => id, id => current.Ownership.Single(x => x.AssetId == id).Version, StringComparer.Ordinal);
            return new VehicleResaleEngine(current, authority, releaseGuard, world).Sell(new SellVehicleCommand
            {
                CommandId = commandId,
                RequesterId = player.PlayerId,
                QuoteId = quote.QuoteId,
                AssetId = quote.AssetId,
                ExpectedFleetVersion = fleet.Version,
                ExpectedOwnershipVersion = ownership.Version,
                ExpectedQuoteVersion = quote.Version,
                ExpectedWalletVersion = wallet.Version,
                ExpectedFleetVersions = fleetVersions,
                ExpectedOwnershipVersions = ownershipVersions
            });
        }
    }

    public IReadOnlyList<VehicleResaleRecord> ReconcilePendingResales(INetworkRoleDetector authority, IAssetReleaseGuard releaseGuard, IExistingVehicleOwnershipAdapter world)
    {
        lock (gate)
        {
            if (current == null) throw new InvalidOperationException("Runtime state is not initialized from SaveGameData.");
            var engine = new VehicleResaleEngine(current, authority, releaseGuard, world);
            return current.Resales.Where(x => x.State == ResaleState.ReconcileRequired).Select(x => x.CommandId).ToArray().Select(engine.Reconcile).ToArray();
        }
    }

    public FinancingPool RegisterLocalFinancingPool(string commandId, string poolId, long backedCapital, INetworkRoleDetector authority)
    {
        lock (gate) { RequireHost(authority); return new FinancingEngine(current ?? throw new InvalidOperationException("Runtime state is not initialized from SaveGameData."), authority).RegisterPool(commandId, poolId, backedCapital); }
    }

    public FinancingContract OfferLocalFinancing(string commandId, string contractId, FinancingKind kind, bool forCompany, string poolId, long principal,
        int interestBasisPoints, long installment, long intervalTicks, long maturityTicks, long guarantee, INetworkRoleDetector authority)
    {
        lock (gate)
        {
            RequireHost(authority); var player = EnsureLocalPlayer(); var debtor = forCompany ? AccountRef.Company(player.CompanyId ?? throw new InvalidOperationException("Company financing requires membership.")) : AccountRef.Player(player.PlayerId);
            return new FinancingEngine(current!, authority).OfferCredit(commandId, player.PlayerId, contractId, kind, debtor, poolId, principal, interestBasisPoints, installment, intervalTicks, maturityTicks, guarantee);
        }
    }

    public FinancingCommandRecord AcceptLocalFinancing(string commandId, string contractId, INetworkRoleDetector authority)
    {
        lock (gate) { RequireHost(authority); return new FinancingEngine(current!, authority).Accept(commandId, EnsureLocalPlayer().PlayerId, contractId); }
    }

    public FinancingCommandRecord DrawLocalCredit(string commandId, string contractId, long amount, INetworkRoleDetector authority)
    {
        lock (gate) { RequireHost(authority); return new FinancingEngine(current!, authority).Draw(commandId, EnsureLocalPlayer().PlayerId, contractId, amount); }
    }

    public FinancingCommandRecord RepayLocalFinancing(string commandId, string contractId, long amount, INetworkRoleDetector authority)
    {
        lock (gate) { RequireHost(authority); return new FinancingEngine(current!, authority).Repay(commandId, EnsureLocalPlayer().PlayerId, contractId, amount); }
    }

    public long ProcessLocalFinancingClock(string commandId, long tick, INetworkRoleDetector authority)
    {
        lock (gate) { RequireHost(authority); return new FinancingEngine(current!, authority).ProcessClock(commandId, tick); }
    }

    public TriagePlan CreateLocalTriagePlan(string commandId, string planId, string assignmentId, IReadOnlyList<string> orderedTrackIds, INetworkRoleDetector authority)
        => CreateTriagePlanFor(commandId, EnsureLocalPlayer().PlayerId, planId, assignmentId, orderedTrackIds, authority);

    public TriagePlan CreateTriagePlanFor(string commandId, string playerId, string planId, string assignmentId, IReadOnlyList<string> orderedTrackIds, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); return new TriageAssistanceEngine(current!, authority, new DisabledSelfShuntTriagePort()).CreatePlan(commandId, EnsurePersistentPlayer(playerId).PlayerId, planId, assignmentId, TriageAssistanceLevel.PlanningOnly, orderedTrackIds); } }

    public TriagePlan CancelLocalTriagePlan(string commandId, string planId, INetworkRoleDetector authority)
        => CancelTriagePlanFor(commandId, EnsureLocalPlayer().PlayerId, planId, authority);

    public TriagePlan CancelTriagePlanFor(string commandId, string playerId, string planId, INetworkRoleDetector authority)
    { lock (gate) { RequireHost(authority); return new TriageAssistanceEngine(current!, authority, new DisabledSelfShuntTriagePort()).Cancel(commandId, EnsurePersistentPlayer(playerId).PlayerId, planId); } }

    private static VehicleAcquisitionSnapshot Empty(string checkpointId) => new VehicleAcquisitionSnapshot
    {
        CheckpointId = checkpointId,
        Economy = new CompanyEconomySnapshot { CheckpointId = checkpointId },
        Assets = new AssetRegistrySnapshot(),
        Ownership = new List<AssetOwnership>(),
        Offers = new List<VehicleOffer>(),
        Acquisitions = new List<AcquisitionRecord>(),
        Fleet = new List<FleetAssetState>(),
        FleetCommands = new List<FleetCommandRecord>(),
        ResaleQuotes = new List<VehicleResaleQuote>(),
        Resales = new List<VehicleResaleRecord>()
    };

    private static Dictionary<string, long> GovernanceVersions(CompanyState company, PlayerEconomicState player, MembershipRequest request) => new Dictionary<string, long>
    {
        ["company:" + company.CompanyId] = company.Version,
        ["player:" + player.PlayerId] = player.Version,
        ["membership:" + request.RequestId] = request.Version
    };

    private static void RequireHost(INetworkRoleDetector authority)
    {
        if (authority == null) throw new InvalidOperationException("Host authority is required.");
        if (!NetworkAuthorityPolicy.CanExecuteEconomy(authority.Detect(), out var reason)) throw new InvalidOperationException(reason);
    }
}
