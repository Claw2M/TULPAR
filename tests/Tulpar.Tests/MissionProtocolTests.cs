using System.Globalization;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;

namespace Tulpar.Tests;

public class MissionProtocolTests
{
    private const byte VehicleSysId = 1;
    private const byte VehicleCompId = 1;
    private const byte GcsSysId = 255;
    private const byte GcsCompId = 190;

    private static List<MavlinkFrame> ParseOutgoingPacket(byte[] bytes)
    {
        var parser = new MavlinkParser();
        return parser.Feed(bytes).ToList();
    }

    [Fact]
    public async Task UploadMission_HandlesLegacyMissionRequestAndDuplicateOutOfOrderSeq_AndCompletesOnAck()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 250, maxRetries: 2);
        var items = new List<MissionItem>
        {
            new(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavTakeoff, 15f, 0f, 0f, 0f, 41.0082376, 28.9783589, 30f, true),
            new(1, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0f, 5f, 0f, 90f, 41.0123456, 28.9854321, 50f, true),
            new(2, MavFrame.GlobalInt, MavCmd.NavReturnToLaunch, 0f, 0f, 0f, 0f, 41.0082376, 28.9783589, 120f, false)
        };

        var requestedOrder = new ushort[] { 0, 2, 0, 1 };
        int requestStep = 0;
        var uploadedItems = new List<MissionItemIntMessage>();
        int missionCountPackets = 0;
        byte vehicleSeq = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            foreach (MavlinkFrame frame in ParseOutgoingPacket(bytes))
            {
                Assert.Equal(GcsSysId, frame.SystemId);
                Assert.Equal(GcsCompId, frame.ComponentId);

                if (frame.MessageId == MissionCountMessage.MsgId)
                {
                    var countMsg = MissionCountMessage.Decode(frame);
                    Assert.Equal(VehicleSysId, countMsg.TargetSystem);
                    Assert.Equal(VehicleCompId, countMsg.TargetComponent);
                    Assert.Equal((ushort)3, countMsg.Count);
                    Assert.Equal(MavMissionType.Mission, countMsg.MissionType);
                    missionCountPackets++;

                    // First request arrives as legacy MAVLink v1 MISSION_REQUEST (#40, no mission_type byte).
                    var req0 = new MissionRequestMessage(GcsSysId, GcsCompId, requestedOrder[requestStep++]);
                    byte[] rawV1 = MavlinkEncoder.EncodeV1(
                        VehicleSysId,
                        VehicleCompId,
                        vehicleSeq++,
                        (byte)MissionRequestMessage.MsgId,
                        req0.EncodePayload().AsSpan(0, 4),
                        MissionRequestMessage.MsgCrcExtra);
                    manager.HandleIncomingFrame(ParseOutgoingPacket(rawV1).Single());
                }
                else if (frame.MessageId == MissionItemIntMessage.MsgId)
                {
                    var itemMsg = MissionItemIntMessage.Decode(frame);
                    uploadedItems.Add(itemMsg);

                    if (requestStep < requestedOrder.Length)
                    {
                        ushort nextSeq = requestedOrder[requestStep++];
                        IMavlinkMessage nextReq = nextSeq == 0
                            ? new MissionRequestMessage(GcsSysId, GcsCompId, nextSeq)
                            : new MissionRequestIntMessage(GcsSysId, GcsCompId, nextSeq, MavMissionType.Mission);

                        byte[] rawNext = MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, nextReq);
                        manager.HandleIncomingFrame(ParseOutgoingPacket(rawNext).Single());
                    }
                    else
                    {
                        var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Mission);
                        byte[] rawAck = MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, ack);
                        manager.HandleIncomingFrame(ParseOutgoingPacket(rawAck).Single());
                    }
                }
            }

            return Task.CompletedTask;
        }

        var progressReports = new List<MissionTransferProgress>();
        var progress = new RecordingProgress(progressReports);

        await manager.UploadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Mission,
            items,
            SendAsync,
            progress,
            CancellationToken.None);

        Assert.Equal(1, missionCountPackets);
        Assert.Equal(new ushort[] { 0, 2, 0, 1 }, uploadedItems.Select(x => x.Seq).ToArray());

        // Duplicate seq 0 must not push CompletedCount past TotalCount (3).
        Assert.Equal("Starting", progressReports.First().Stage);
        Assert.Equal("Completed", progressReports.Last().Stage);
        Assert.Equal(3, progressReports.Last().CompletedCount);
        Assert.All(progressReports, p => Assert.InRange(p.CompletedCount, 0, p.TotalCount));

        MissionItemIntMessage wp1 = uploadedItems.Last(x => x.Seq == 1);
        Assert.Equal(MavFrame.GlobalRelativeAltInt, wp1.Frame);
        Assert.Equal(MavCmd.NavWaypoint, wp1.Command);
        Assert.Equal(410123456, wp1.X);
        Assert.Equal(289854321, wp1.Y);
        Assert.Equal(50f, wp1.Z);
        Assert.Equal((byte)1, wp1.Autocontinue);
        Assert.Equal(90f, wp1.Param4);

        MissionItemIntMessage rtl = uploadedItems.First(x => x.Seq == 2);
        Assert.Equal(MavFrame.GlobalInt, rtl.Frame);
        Assert.Equal(MavCmd.NavReturnToLaunch, rtl.Command);
        Assert.Equal((byte)0, rtl.Autocontinue);
        Assert.Equal(120f, rtl.Z);
    }

    [Fact]
    public async Task UploadFence_UsesMissionTypeFence_AndRetriesAfterTimeout()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 40, maxRetries: 3);
        var fenceItems = new List<MissionItem>
        {
            new(0, MavFrame.GlobalInt, MavCmd.NavFenceReturnPoint, 0f, 0f, 0f, 0f, 41.0000000, 29.0000000, 0f),
            new(1, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion, 3f, 0f, 0f, 0f, 41.0010000, 29.0000000, 0f),
            new(2, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion, 3f, 0f, 0f, 0f, 41.0010000, 29.0020000, 0f),
            new(3, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion, 3f, 0f, 0f, 0f, 40.9990000, 29.0010000, 0f)
        };

        int countAttempts = 0;
        var receivedFenceSeqs = new List<ushort>();
        byte vehicleSeq = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            foreach (MavlinkFrame frame in ParseOutgoingPacket(bytes))
            {
                if (frame.MessageId == MissionCountMessage.MsgId)
                {
                    var countMsg = MissionCountMessage.Decode(frame);
                    Assert.Equal(MavMissionType.Fence, countMsg.MissionType);
                    Assert.Equal((ushort)4, countMsg.Count);
                    countAttempts++;

                    // Drop first MISSION_COUNT attempt to force timeout retry.
                    if (countAttempts == 1)
                    {
                        return Task.CompletedTask;
                    }

                    // Inject unrelated v2 MISSION requests (both MISSION_REQUEST_INT and zero-truncated legacy MISSION_REQUEST)
                    // which must be ignored during Fence upload, then send valid Fence seq 0.
                    var strayInt = new MissionRequestIntMessage(GcsSysId, GcsCompId, 99, MavMissionType.Mission);
                    manager.HandleIncomingFrame(ParseOutgoingPacket(
                        MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, strayInt)).Single());

                    var strayV2Legacy = new MissionRequestMessage(GcsSysId, GcsCompId, 98, MavMissionType.Mission);
                    manager.HandleIncomingFrame(ParseOutgoingPacket(
                        MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, strayV2Legacy)).Single());

                    var req0 = new MissionRequestIntMessage(GcsSysId, GcsCompId, 0, MavMissionType.Fence);
                    manager.HandleIncomingFrame(ParseOutgoingPacket(
                        MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, req0)).Single());
                }
                else if (frame.MessageId == MissionItemIntMessage.MsgId)
                {
                    var itemMsg = MissionItemIntMessage.Decode(frame);
                    Assert.Equal(MavMissionType.Fence, itemMsg.MissionType);
                    receivedFenceSeqs.Add(itemMsg.Seq);

                    if (itemMsg.Seq < 3)
                    {
                        ushort next = (ushort)(itemMsg.Seq + 1);
                        // Also verify legacy MAVLink v1 MISSION_REQUEST (4-byte payload, no mission_type) matches active Fence transfer.
                        if (next == 1)
                        {
                            var legacyReq = new MissionRequestMessage(GcsSysId, GcsCompId, next);
                            byte[] rawV1 = MavlinkEncoder.EncodeV1(
                                VehicleSysId,
                                VehicleCompId,
                                vehicleSeq++,
                                (byte)MissionRequestMessage.MsgId,
                                legacyReq.EncodePayload().AsSpan(0, 4),
                                MissionRequestMessage.MsgCrcExtra);
                            manager.HandleIncomingFrame(ParseOutgoingPacket(rawV1).Single());
                        }
                        else
                        {
                            var req = new MissionRequestIntMessage(GcsSysId, GcsCompId, next, MavMissionType.Fence);
                            manager.HandleIncomingFrame(ParseOutgoingPacket(
                                MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, req)).Single());
                        }
                    }
                    else
                    {
                        var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Fence);
                        manager.HandleIncomingFrame(ParseOutgoingPacket(
                            MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, ack)).Single());
                    }
                }
            }

            return Task.CompletedTask;
        }

        await manager.UploadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Fence,
            fenceItems,
            SendAsync,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, countAttempts);
        Assert.Equal(new ushort[] { 0, 1, 2, 3 }, receivedFenceSeqs);
    }

    [Fact]
    public async Task UploadMission_EmptyList_SendsCountZeroAndCompletesOnAck()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 100, maxRetries: 1);
        ushort? sentCount = null;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            MavlinkFrame frame = ParseOutgoingPacket(bytes).Single();
            Assert.Equal(MissionCountMessage.MsgId, frame.MessageId);
            sentCount = MissionCountMessage.Decode(frame).Count;

            var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Mission);
            manager.HandleIncomingFrame(ParseOutgoingPacket(
                MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, ack)).Single());
            return Task.CompletedTask;
        }

        await manager.UploadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Mission,
            Array.Empty<MissionItem>(),
            SendAsync,
            progress: null,
            CancellationToken.None);

        Assert.Equal((ushort)0, sentCount);
    }

    [Fact]
    public async Task UploadMission_ThrowsMissionProtocolException_WhenVehicleRejectsAck()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 100, maxRetries: 1);
        var items = new[]
        {
            new MissionItem(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0, 0, 0, 0, 41.0, 29.0, 50f)
        };

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.NoSpace, MavMissionType.Mission);
            manager.HandleIncomingFrame(ParseOutgoingPacket(
                MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, ack)).Single());
            return Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<MissionProtocolException>(() =>
            manager.UploadAsync(VehicleSysId, VehicleCompId, MavMissionType.Mission, items, SendAsync, null, CancellationToken.None));

        Assert.Equal(MavMissionResult.NoSpace, ex.Result);
    }

    [Fact]
    public async Task UploadMission_ThrowsTimeoutException_WhenRetriesExhausted()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 25, maxRetries: 2);
        var items = new[]
        {
            new MissionItem(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0, 0, 0, 0, 41.0, 29.0, 50f)
        };
        int sendCount = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            sendCount++;
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<TimeoutException>(() =>
            manager.UploadAsync(VehicleSysId, VehicleCompId, MavMissionType.Mission, items, SendAsync, null, CancellationToken.None));

        Assert.Equal(3, sendCount);
    }

    [Fact]
    public async Task DownloadMission_RequestsAllSeqsAndReturnsOrderedItems()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 150, maxRetries: 2);
        var vehicleItems = new[]
        {
            new MissionItemIntMessage(GcsSysId, GcsCompId, 0, MavFrame.GlobalInt, MavCmd.NavWaypoint, 1, 1, 0f, 0f, 0f, 0f, 410082376, 289783589, 105.5f, MavMissionType.Mission),
            new MissionItemIntMessage(GcsSysId, GcsCompId, 1, MavFrame.GlobalRelativeAltInt, MavCmd.NavLoiterTime, 0, 1, 10f, 0f, 50f, 0f, 410100000, 289800000, 60f, MavMissionType.Mission),
            new MissionItemIntMessage(GcsSysId, GcsCompId, 2, MavFrame.GlobalRelativeAltInt, MavCmd.NavReturnToLaunch, 0, 0, 0f, 0f, 0f, 0f, 410082376, 289783589, 0f, MavMissionType.Mission)
        };

        var requestedSeqs = new List<ushort>();
        MissionAckMessage? finalAck = null;
        byte vehicleSeq = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            foreach (MavlinkFrame frame in ParseOutgoingPacket(bytes))
            {
                if (frame.MessageId == MissionRequestListMessage.MsgId)
                {
                    var reqList = MissionRequestListMessage.Decode(frame);
                    Assert.Equal(MavMissionType.Mission, reqList.MissionType);

                    var countMsg = new MissionCountMessage(GcsSysId, GcsCompId, (ushort)vehicleItems.Length, MavMissionType.Mission);
                    manager.HandleIncomingFrame(ParseOutgoingPacket(
                        MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, countMsg)).Single());
                }
                else if (frame.MessageId == MissionRequestIntMessage.MsgId)
                {
                    var reqItem = MissionRequestIntMessage.Decode(frame);
                    Assert.Equal(MavMissionType.Mission, reqItem.MissionType);
                    requestedSeqs.Add(reqItem.Seq);

                    if (reqItem.Seq == 1)
                    {
                        // Inject a stale duplicate of seq 0 first; manager must skip it and wait for seq 1.
                        manager.HandleIncomingFrame(ParseOutgoingPacket(
                            MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, vehicleItems[0])).Single());
                    }

                    if (reqItem.Seq == 2)
                    {
                        // Verify legacy MISSION_ITEM (#39) fallback works during download too.
                        var src = vehicleItems[2];
                        var legacyItem = new MissionItemMessage(
                            src.TargetSystem,
                            src.TargetComponent,
                            src.Seq,
                            src.Frame,
                            src.Command,
                            src.Current,
                            src.Autocontinue,
                            src.Param1,
                            src.Param2,
                            src.Param3,
                            src.Param4,
                            src.X / 1e7f,
                            src.Y / 1e7f,
                            src.Z,
                            src.MissionType);
                        manager.HandleIncomingFrame(ParseOutgoingPacket(
                            MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, legacyItem)).Single());
                    }
                    else
                    {
                        manager.HandleIncomingFrame(ParseOutgoingPacket(
                            MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, vehicleItems[reqItem.Seq])).Single());
                    }
                }
                else if (frame.MessageId == MissionAckMessage.MsgId)
                {
                    finalAck = MissionAckMessage.Decode(frame);
                }
            }

            return Task.CompletedTask;
        }

        IReadOnlyList<MissionItem> downloaded = await manager.DownloadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Mission,
            SendAsync,
            progress: null,
            CancellationToken.None);

        Assert.Equal(new ushort[] { 0, 1, 2 }, requestedSeqs);
        Assert.NotNull(finalAck);
        Assert.Equal(MavMissionResult.Accepted, finalAck.Type);
        Assert.Equal(MavMissionType.Mission, finalAck.MissionType);

        Assert.Equal(3, downloaded.Count);
        Assert.Equal(0, downloaded[0].Seq);
        Assert.Equal(MavFrame.GlobalInt, downloaded[0].Frame);
        Assert.Equal(41.0082376, downloaded[0].Latitude, 7);
        Assert.Equal(28.9783589, downloaded[0].Longitude, 7);
        Assert.Equal(105.5f, downloaded[0].Altitude);

        Assert.Equal(1, downloaded[1].Seq);
        Assert.Equal(MavCmd.NavLoiterTime, downloaded[1].Command);
        Assert.Equal(10f, downloaded[1].Param1);
        Assert.Equal(50f, downloaded[1].Param3);

        Assert.Equal(2, downloaded[2].Seq);
        Assert.Equal(MavCmd.NavReturnToLaunch, downloaded[2].Command);
        Assert.False(downloaded[2].Autocontinue);
        Assert.Equal(41.0082376, downloaded[2].Latitude, 5);
    }

    [Fact]
    public async Task DownloadFence_WhenEmpty_SendsAcceptedAckAndReturnsEmptyList()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 100, maxRetries: 1);
        MissionAckMessage? sentAck = null;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            MavlinkFrame frame = ParseOutgoingPacket(bytes).Single();
            if (frame.MessageId == MissionRequestListMessage.MsgId)
            {
                var reqList = MissionRequestListMessage.Decode(frame);
                Assert.Equal(MavMissionType.Fence, reqList.MissionType);

                var count = new MissionCountMessage(GcsSysId, GcsCompId, 0, MavMissionType.Fence);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, count)).Single());
            }
            else if (frame.MessageId == MissionAckMessage.MsgId)
            {
                sentAck = MissionAckMessage.Decode(frame);
            }

            return Task.CompletedTask;
        }

        IReadOnlyList<MissionItem> result = await manager.DownloadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Fence,
            SendAsync,
            progress: null,
            CancellationToken.None);

        Assert.Empty(result);
        Assert.NotNull(sentAck);
        Assert.Equal(MavMissionResult.Accepted, sentAck.Type);
        Assert.Equal(MavMissionType.Fence, sentAck.MissionType);
    }

    [Fact]
    public async Task DownloadFence_IgnoresZeroTruncatedV2MissionItemForMissionType()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 150, maxRetries: 1);
        byte vehicleSeq = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            MavlinkFrame frame = ParseOutgoingPacket(bytes).Single();
            if (frame.MessageId == MissionRequestListMessage.MsgId)
            {
                var count = new MissionCountMessage(GcsSysId, GcsCompId, 1, MavMissionType.Fence);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, count)).Single());
            }
            else if (frame.MessageId == MissionRequestIntMessage.MsgId)
            {
                // Stray v2 zero-truncated MISSION_ITEM (#39) with MissionType = Mission (0) must NOT satisfy Fence seq 0.
                var strayMissionItem = new MissionItemMessage(
                    GcsSysId, GcsCompId, 0, MavFrame.GlobalInt, MavCmd.NavWaypoint,
                    0, 1, 0f, 0f, 0f, 0f, 10f, 20f, 999f, MavMissionType.Mission);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, strayMissionItem)).Single());

                var realFenceItem = new MissionItemIntMessage(
                    GcsSysId, GcsCompId, 0, MavFrame.GlobalInt, MavCmd.NavFenceReturnPoint,
                    0, 1, 0f, 0f, 0f, 0f, 410082376, 289783589, 50f, MavMissionType.Fence);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, realFenceItem)).Single());
            }

            return Task.CompletedTask;
        }

        IReadOnlyList<MissionItem> downloaded = await manager.DownloadAsync(
            VehicleSysId,
            VehicleCompId,
            MavMissionType.Fence,
            SendAsync,
            progress: null,
            CancellationToken.None);

        Assert.Single(downloaded);
        Assert.Equal(MavCmd.NavFenceReturnPoint, downloaded[0].Command);
        Assert.Equal(41.0082376, downloaded[0].Latitude, 7);
        Assert.Equal(28.9783589, downloaded[0].Longitude, 7);
        Assert.Equal(50f, downloaded[0].Altitude);
    }

    [Fact]
    public async Task ClearAll_SendsClearAllAndAwaitsAcceptedAck()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 40, maxRetries: 2);
        int clearAttempts = 0;

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            MavlinkFrame frame = ParseOutgoingPacket(bytes).Single();
            Assert.Equal(MissionClearAllMessage.MsgId, frame.MessageId);
            var clearMsg = MissionClearAllMessage.Decode(frame);
            Assert.Equal(VehicleSysId, clearMsg.TargetSystem);
            Assert.Equal(VehicleCompId, clearMsg.TargetComponent);
            Assert.Equal(MavMissionType.Fence, clearMsg.MissionType);

            clearAttempts++;
            if (clearAttempts == 2)
            {
                var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Fence);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, ack)).Single());
            }

            return Task.CompletedTask;
        }

        await manager.ClearAllAsync(VehicleSysId, VehicleCompId, MavMissionType.Fence, SendAsync, CancellationToken.None);
        Assert.Equal(2, clearAttempts);
    }

    [Fact]
    public void QgcWpl110_And_Json_RoundTrip_PreservesRelativeAndAmslFramesAndCoordinates()
    {
        var missionItems = new List<MissionItem>
        {
            new(0, MavFrame.GlobalInt, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, 41.0082376, 28.9783589, 125.5f, true),
            new(1, MavFrame.GlobalRelativeAltInt, MavCmd.NavLoiterTurns, 3f, 0f, 80f, 1f, -33.8688197, 151.2092955, 75.25f, false)
        };

        var fenceItems = new List<MissionItem>
        {
            new(0, MavFrame.GlobalInt, MavCmd.NavFenceReturnPoint, 0f, 0f, 0f, 0f, 41.0082376, 28.9783589, 50f, true),
            new(1, MavFrame.GlobalInt, MavCmd.NavFenceCircleInclusion, 250f, 0f, 0f, 0f, 41.0090000, 28.9790000, 0f, true)
        };

        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // Force Turkish locale (comma decimal separator) to prove InvariantCulture formatting/parsing.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            string wpl = MissionFileCodec.ExportQgcWpl110(missionItems);
            string[] lines = wpl.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("QGC WPL 110", lines[0]);
            Assert.Equal(3, lines.Length);

            string[] row0Cols = lines[1].Split('\t');
            Assert.Equal(12, row0Cols.Length);
            Assert.Equal("0", row0Cols[2]); // MAV_FRAME_GLOBAL_INT normalized to MAV_FRAME_GLOBAL (0) in WPL 110
            Assert.Equal("41.0082376", row0Cols[8]);
            Assert.Equal("28.9783589", row0Cols[9]);
            Assert.Equal("125.5", row0Cols[10]);

            string[] row1Cols = lines[2].Split('\t');
            Assert.Equal(12, row1Cols.Length);
            Assert.Equal("3", row1Cols[2]); // MAV_FRAME_GLOBAL_RELATIVE_ALT_INT normalized to MAV_FRAME_GLOBAL_RELATIVE_ALT (3)
            Assert.Equal("0", row1Cols[11]);

            IReadOnlyList<MissionItem> importedWpl = MissionFileCodec.ImportQgcWpl110(wpl);
            Assert.Equal(2, importedWpl.Count);

            Assert.Equal(missionItems[0], importedWpl[0]);
            Assert.Equal(missionItems[1], importedWpl[1]);

            // Also verify explicit CRLF external WPL file with frames 0 and 3 imports as GlobalInt / GlobalRelativeAltInt.
            string externalWpl = "QGC WPL 110\r\n0\t1\t0\t16\t0\t0\t0\t0\t40.123456\t32.654321\t900.5\t1\r\n1\t0\t3\t22\t15\t0\t0\t0\t40.2\t32.7\t45\t1\r\n";
            IReadOnlyList<MissionItem> parsedExternal = MissionFileCodec.ImportQgcWpl110(externalWpl);
            Assert.Equal(MavFrame.GlobalInt, parsedExternal[0].Frame);
            Assert.Equal(MavFrame.GlobalRelativeAltInt, parsedExternal[1].Frame);
            Assert.Equal(MavCmd.NavTakeoff, parsedExternal[1].Command);
            Assert.Equal(15f, parsedExternal[1].Param1);

            // JSON round-trip for complete MissionDocument (items + fence).
            var doc = new MissionDocument(MavMissionType.Mission, missionItems, fenceItems);
            string json = MissionFileCodec.ExportJson(doc);
            MissionDocument importedDoc = MissionFileCodec.ImportJson(json);

            Assert.Equal(doc.MissionType, importedDoc.MissionType);
            Assert.Equal(doc.Items, importedDoc.Items);
            Assert.Equal(doc.FenceItems, importedDoc.FenceItems);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ImportQgcWpl110_InvalidHeader_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => MissionFileCodec.ImportQgcWpl110("NOT A WPL FILE\n0\t1\t3\t16\t0\t0\t0\t0\t0\t0\t0\t1"));
    }

    [Fact]
    public void ImportQgcWpl110_MalformedColumnsOrComments_BehavesCorrectly()
    {
        string withComment = "QGC WPL 110\n# comment line\n0\t1\t3\t16\t0\t0\t0\t0\t41.0\t29.0\t50\t1\n";
        IReadOnlyList<MissionItem> parsed = MissionFileCodec.ImportQgcWpl110(withComment);
        Assert.Single(parsed);

        Assert.Throws<FormatException>(() => MissionFileCodec.ImportQgcWpl110("QGC WPL 110\n0\t1\t3\t16\t0"));
        Assert.Throws<FormatException>(() => MissionFileCodec.ImportQgcWpl110("QGC WPL 110\n0\t1\t3\t16\t0\t0\t0\t0\tBAD\t29\t50\t1"));
    }

    [Fact]
    public async Task UploadMission_NullElement_ThrowsArgumentExceptionWithoutSending()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 50, maxRetries: 1);
        var items = new MissionItem[] { null! };
        bool sentAny = false;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.UploadAsync(
                VehicleSysId,
                VehicleCompId,
                MavMissionType.Mission,
                items,
                (_, _) => { sentAny = true; return Task.CompletedTask; },
                progress: null,
                CancellationToken.None));

        Assert.False(sentAny);
    }

    [Fact]
    public async Task ClearAll_ThrowsMissionProtocolException_WhenVehicleDenies()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 100, maxRetries: 1);

        Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Denied, MavMissionType.Mission);
            manager.HandleIncomingFrame(ParseOutgoingPacket(
                MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, ack)).Single());
            return Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<MissionProtocolException>(() =>
            manager.ClearAllAsync(VehicleSysId, VehicleCompId, MavMissionType.Mission, SendAsync, CancellationToken.None));

        Assert.Equal(MavMissionResult.Denied, ex.Result);
    }

    [Fact]
    public async Task DownloadAsync_CancelledDuringWait_ThrowsOperationCanceledException()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 2000, maxRetries: 3);
        using var cts = new CancellationTokenSource(millisecondsDelay: 30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadAsync(
                VehicleSysId,
                VehicleCompId,
                MavMissionType.Mission,
                (_, _) => Task.CompletedTask,
                progress: null,
                cts.Token));
    }

    [Fact]
    public async Task ConcurrentOperations_OnSameManager_SerializeWithoutCrossTalk()
    {
        var manager = new MissionProtocolManager(GcsSysId, GcsCompId, stepTimeoutMs: 200, maxRetries: 2);
        var mission = new[]
        {
            new MissionItem(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0, 0, 0, 0, 41.0, 29.0, 40f)
        };
        var log = new List<string>();
        byte vehicleSeq = 0;

        async Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            MavlinkFrame frame = ParseOutgoingPacket(bytes).Single();
            if (frame.MessageId == MissionCountMessage.MsgId)
            {
                log.Add("upload:count");
                await Task.Delay(15, ct);
                var req = new MissionRequestIntMessage(GcsSysId, GcsCompId, 0, MavMissionType.Mission);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, req)).Single());
            }
            else if (frame.MessageId == MissionItemIntMessage.MsgId)
            {
                log.Add("upload:item");
                var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Mission);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, ack)).Single());
            }
            else if (frame.MessageId == MissionClearAllMessage.MsgId)
            {
                log.Add("clear:send");
                var ack = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Accepted, MavMissionType.Fence);
                manager.HandleIncomingFrame(ParseOutgoingPacket(
                    MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, vehicleSeq++, ack)).Single());
            }
        }

        // Inject foreign-system and non-mission frames concurrently before/during transfers.
        var foreignAck = new MissionAckMessage(GcsSysId, GcsCompId, MavMissionResult.Error, MavMissionType.Mission);
        manager.HandleIncomingFrame(ParseOutgoingPacket(MavlinkEncoder.EncodeV2(99, 1, 0, foreignAck)).Single());
        var hb = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 0, MavState.Active);
        manager.HandleIncomingFrame(ParseOutgoingPacket(MavlinkEncoder.EncodeV2(VehicleSysId, VehicleCompId, 0, hb)).Single());

        Task t1 = manager.UploadAsync(VehicleSysId, VehicleCompId, MavMissionType.Mission, mission, SendAsync, null, CancellationToken.None);
        Task t2 = manager.ClearAllAsync(VehicleSysId, VehicleCompId, MavMissionType.Fence, SendAsync, CancellationToken.None);
        await Task.WhenAll(t1, t2);

        Assert.Equal(3, log.Count);
        Assert.Equal(["upload:count", "upload:item", "clear:send"], log);
    }

    private sealed class RecordingProgress(List<MissionTransferProgress> reports) : IProgress<MissionTransferProgress>
    {
        public void Report(MissionTransferProgress value) => reports.Add(value);
    }
}
