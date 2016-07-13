﻿using System.Collections.Generic;

namespace Pulsus.Gameplay
{
	public abstract class Judge : EventPlayer
	{
		protected List<NoteScore> noteScores;
		protected List<NoteScore> pendingNoteScores;

		protected double processAheadTime = 0.0;
		protected double missWindow;

		protected Dictionary<int, int> lastNote = new Dictionary<int, int>();

		protected double judgeTime { get { return currentTime - processAheadTime; } }

		public Judge(Song song)
			: base(song)
		{
			if (song == null || chart == null)
				return;

			noteScores = new List<NoteScore>(chart.playerEventCount);
			pendingNoteScores = new List<NoteScore>(chart.playerEventCount);
		}

		public override void StartPlayer()
		{
			startOffset += processAheadTime;
			base.StartPlayer();
		}

		public override void UpdateSong()
		{
			base.UpdateSong();

			for (int i = 0; i < pendingNoteScores.Count; i++)
			{
				NoteScore noteScore = pendingNoteScores[i];

				if (judgeTime > noteScore.timestamp + missWindow)
					JudgeNote(judgeTime, noteScore);
			}
		}

		public override void OnSongEnd()
		{
			// prevent judge from stopping prematurely, as the processAheadTime
			// affects the time how much ahead the judge is from other players.
		}

		public override void OnPlayerKey(NoteEvent noteEvent)
		{
			pendingNoteScores.Add(new NoteScore(noteEvent, noteEvent.timestamp, NoteJudgeType.JudgePress));
		}

		public override void OnPlayerKeyLong(NoteEvent noteEvent)
		{
			pendingNoteScores.Add(new NoteScore(noteEvent, noteEvent.timestamp, NoteJudgeType.JudgePress));
		}

		public override void OnPlayerKeyLongEnd(LongNoteEndEvent noteEndEvent)
		{
			pendingNoteScores.Add(new NoteScore(noteEndEvent, noteEndEvent.timestamp, NoteJudgeType.JudgeRelease));
		}

		public override void OnBPM(BPMEvent bpmEvent)
		{
			base.OnBPM(bpmEvent);

			if (nextBpm < 0)
				nextBpm = -nextBpm;
		}

		public virtual void NotePlayed(double hitTimestamp, NoteScore noteScore)
		{
			pendingNoteScores.Remove(noteScore);

			noteScore.hitOffset = hitTimestamp - noteScore.timestamp;
			noteScores.Add(noteScore);
		}

		public bool HasJudged(NoteEvent note)
		{
			foreach (NoteScore noteScore in pendingNoteScores)
			{
				if (noteScore.noteEvent == note)
					return false;
			}
			return true;
		}

		public abstract void JudgeNote(double hitTimestamp, NoteScore noteScore);
	}

	public enum NoteJudgeType
	{
		JudgePress,
		JudgeRelease,
	}
}
