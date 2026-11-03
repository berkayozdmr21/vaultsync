import test from "node:test";
import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  mkdirSync,
  writeFileSync,
  readFileSync,
  existsSync,
  readdirSync,
} from "node:fs";
import { resolve, join } from "node:path";
const base = process.env.API_URL || "http://127.0.0.1:9251";
const owner = process.env.OWNER_KEY || "vaultsync-local-dev";
const hash = (b) => createHash("sha256").update(b).digest("hex");
async function api(path, method = "GET", body, key = owner) {
  const r = await fetch(base + "/api" + path, {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(key ? { "X-Vault-Key": key } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: r.status, data: await r.json().catch(() => null) };
}
async function put(bytes, key, id = hash(bytes)) {
  const r = await fetch(base + "/api/chunks/" + id, {
    method: "PUT",
    headers: { "X-Vault-Key": key },
    body: bytes,
  });
  return r.status;
}
test("encrypted, device-scoped versioned backup", async (t) => {
  let device, snapshot, token;
  const bytes = Buffer.from("versioned backup content " + randomUUID()),
    id = hash(bytes),
    input = {
      key: randomUUID(),
      parent: null,
      files: [
        { path: "docs/notes.txt", bytes: bytes.length, hash: id, chunks: [id] },
      ],
    };
  await t.test("anonymous access rejected", async () =>
    assert.equal((await api("/overview", "GET", undefined, null)).status, 401),
  );
  await t.test("owner creates device", async () => {
    const r = await api("/devices", "POST", { name: "Test " + randomUUID() });
    assert.equal(r.status, 200);
    device = r.data;
    token = device.key;
    assert.equal((await api("/overview", "GET", undefined, token)).status, 401);
  });
  await t.test("bad chunk digest rejected", async () =>
    assert.equal(await put(bytes, token, "0".repeat(64)), 400),
  );
  await t.test("chunk retries do not duplicate bytes", async () => {
    assert.equal(await put(bytes, token), 204);
    const before = (await api("/overview")).data.physicalBytes;
    assert.equal(await put(bytes, token), 204);
    assert.equal((await api("/overview")).data.physicalBytes, before);
  });
  await t.test("verified manifest commits atomically", async () => {
    const r = await api("/snapshots", "POST", input, token);
    assert.equal(r.status, 200, JSON.stringify(r.data));
    snapshot = r.data;
    assert.equal(
      (await api("/device", "GET", undefined, token)).data.head,
      snapshot.id,
    );
  });
  await t.test("lost response retry returns original snapshot", async () =>
    assert.equal(
      (await api("/snapshots", "POST", input, token)).data.id,
      snapshot.id,
    ),
  );
  await t.test("stale parent conflicts", async () =>
    assert.equal(
      (await api("/snapshots", "POST", { ...input, key: randomUUID() }, token))
        .status,
      409,
    ),
  );
  await t.test("unsafe paths rejected", async () => {
    for (const path of ["../private", "/absolute", "C:/file", "a/../b", "a\\b"])
      assert.equal(
        (
          await api(
            "/snapshots",
            "POST",
            {
              key: randomUUID(),
              parent: snapshot.id,
              files: [{ ...input.files[0], path }],
            },
            token,
          )
        ).status,
        400,
      );
  });
  await t.test("manifest mismatch never advances head", async () => {
    assert.equal(
      (
        await api(
          "/snapshots",
          "POST",
          {
            key: randomUUID(),
            parent: snapshot.id,
            files: [{ ...input.files[0], hash: "0".repeat(64) }],
          },
          token,
        )
      ).status,
      422,
    );
    assert.equal(
      (await api("/device", "GET", undefined, token)).data.head,
      snapshot.id,
    );
  });
  await t.test("device cannot read another source", async () => {
    const other = (await api("/devices", "POST", { name: "Other" })).data;
    assert.equal(
      (await api("/snapshots/" + snapshot.id, "GET", undefined, other.key))
        .status,
      404,
    );
  });
  await t.test("restored bytes exactly match source", async () => {
    const r = await fetch(
      `${base}/api/snapshots/${snapshot.id}/file?path=docs%2Fnotes.txt`,
      { headers: { "X-Vault-Key": token } },
    );
    assert.equal(r.status, 200);
    assert.deepEqual(Buffer.from(await r.arrayBuffer()), bytes);
  });
  await t.test("ciphertext tampering is detected", async () => {
    const root = process.env.TEST_VAULT;
    if (!root) return;
    const path = join(root, "objects", id.slice(0, 2), id),
      original = readFileSync(path);
    assert.ok(!original.includes(bytes));
    const corrupted = Buffer.from(original);
    corrupted[corrupted.length - 1] ^= 1;
    try {
      writeFileSync(path, corrupted);
      assert.equal(
        (
          await fetch(
            `${base}/api/snapshots/${snapshot.id}/file?path=docs%2Fnotes.txt`,
            { headers: { "X-Vault-Key": token } },
          )
        ).status,
        422,
      );
    } finally {
      writeFileSync(path, original);
    }
  });
  await t.test("retention preview never deletes data", async () => {
    const before = (await api("/snapshots")).data.length;
    assert.equal(
      (await api("/retention/preview?keep=1")).data.mode,
      "preview-only",
    );
    assert.equal((await api("/snapshots")).data.length, before);
  });
  await t.test("revoked device cannot reconnect", async () => {
    assert.equal((await api("/devices/" + device.id, "DELETE")).status, 204);
    assert.equal((await api("/device", "GET", undefined, token)).status, 401);
  });
});
test("real agent incremental scan and conflict-preserving restore", async () => {
  const root = resolve(".runtime/agent-tests/" + randomUUID()),
    source = join(root, "source"),
    state = join(root, "state"),
    dest = join(root, "restored");
  mkdirSync(source, { recursive: true });
  mkdirSync(dest, { recursive: true });
  const contents = Buffer.concat([
    Buffer.alloc(1048576, 65),
    Buffer.alloc(50000, 66),
  ]);
  writeFileSync(join(source, "design.bin"), contents);
  writeFileSync(join(source, "empty.txt"), "");
  const device = (await api("/devices", "POST", { name: "Agent integration" }))
    .data;
  const env = {
    ...process.env,
    VAULTSYNC_DEVICE_KEY: device.key,
    VAULTSYNC_API: base,
    VAULTSYNC_STATE: state,
  };
  const exe = process.env.DOTNET_EXE || "dotnet";
  const dll = resolve("agent/bin/Debug/net10.0/VaultSync.Agent.dll");
  function run(args) {
    const r = spawnSync(exe, [dll, ...args], {
      env,
      encoding: "utf8",
      timeout: 120000,
    });
    assert.equal(r.status, 0, r.stderr + "\n" + r.stdout);
    return r.stdout;
  }
  run([source, "--once"]);
  const first = (await api("/snapshots", "GET", undefined, device.key)).data[0];
  assert.equal(first.files, 2);
  assert.match(run([source, "--once"]), /UNCHANGED/);
  contents[contents.length - 1] = 67;
  writeFileSync(join(source, "design.bin"), contents);
  run([source, "--once"]);
  const history = (await api("/snapshots", "GET", undefined, device.key)).data;
  assert.equal(history.length, 2);
  assert.equal(history[0].parent, first.id);
  const diff = (
    await api(
      "/snapshots/" + history[0].id + "/diff",
      "GET",
      undefined,
      device.key,
    )
  ).data;
  assert.deepEqual(diff.changed, ["design.bin"]);
  assert.deepEqual(diff.added, []);
  assert.equal(
    (
      await api(
        "/snapshots/" + history[0].id + "/verify",
        "POST",
        undefined,
        device.key,
      )
    ).data.verified,
    true,
  );
  writeFileSync(join(source, "offline.txt"), "queued while disconnected");
  const offline = spawnSync(exe, [dll, source, "--once"], {
    env: { ...env, VAULTSYNC_API: "http://127.0.0.1:1" },
    encoding: "utf8",
    timeout: 30000,
  });
  assert.notEqual(offline.status, 0);
  assert.ok(existsSync(join(state, "pending.json")));
  run([source, "--once"]);
  assert.equal(
    (await api("/snapshots", "GET", undefined, device.key)).data.length,
    3,
  );
  writeFileSync(join(dest, "design.bin"), "local changes");
  run(["restore", history[0].id, dest]);
  assert.equal(readFileSync(join(dest, "design.bin"), "utf8"), "local changes");
  const copy = readdirSync(dest).find((f) =>
    f.startsWith("design.bin.conflict-"),
  );
  assert.ok(copy);
  assert.deepEqual(readFileSync(join(dest, copy)), contents);
  assert.ok(existsSync(join(dest, "empty.txt")));
});
