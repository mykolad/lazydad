# Similarity experiment

Which method finds better "you might also like" jokes for a joke's own page? This compares two, blind, on production's
jokes. Nothing in the app or the database changes.

| | A: Jev | B: embeddings |
|---|---|---|
| What | [Jev](https://jevtypesafeai.com/) gives each joke a probability for every **topic** (about 20) and every **kind of wordplay** (6), in one `/v1/decide` call with two *choice* questions | `text-embedding-3-small` on `lazydad-openai-resource` gives each joke a vector (512 dimensions) |
| Similar | close probability profiles (cosine; wordplay counts half as much as topic) | close vectors (cosine) |
| Cost | about 350 input tokens per joke, about $0.20 for 1,200 jokes (your Jev credits) | well under a cent |

Both see the same input: the joke and, when it has one, its "why it's funny".

## Running it

1. **The Jev key**, in a file outside the repository (never commit it, never paste it in a chat):
   `%USERPROFILE%\.lazydad\jev-key.txt`, or any file `JEV_KEY_FILE` names.
2. **The embedding model**, once (PowerShell or bash):

   ```powershell
   az cognitiveservices account deployment create -g lazydad-rg -n lazydad-openai-resource `
     --deployment-name text-embedding-3-small --model-name text-embedding-3-small --model-format OpenAI `
     --model-version 1 --sku-name GlobalStandard --sku-capacity 50 -o none
   ```

   Your `az login` calls it (you have `Foundry User` on the resource, runbook section 5).
3. **From the repository root:** `dotnet run experiments/similarity/Similarity.cs`. It reads the jokes from
   `https://lazydad.fyi/jokes`, calls both services, and keeps their answers in `experiments/similarity/out/` (ignored by
   git), so a second run doesn't pay again. It prints what Jev cost and how many suggestions both methods share.
4. **Open `experiments/similarity/out/compare.html`.** For 25 random jokes it shows each method's top 3 as X and Y, in
   random order. Pick the better list for each, then reveal the score: which method was which, and each joke's topic and
   wordplay according to Jev.

The winner becomes the "you might also like" of the joke page (`/j/<id>`), in a separate PR.
